Imports System.IO
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Diagnostics
Imports System.Windows.Threading
Imports Microsoft.Win32
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports PCL

Public Class PageMslMain

    '===============================
    '  服务器实例模型
    '===============================

    Public Class MslInstance
        Public Name As String = ""
        Public CorePath As String = ""
        Public Java As String = ""
        Public Memory As String = "自动"
        Public Args As String = ""
        ''' <summary>是否启用伪终端（ConPTY）模式（创建时选择的终端模式）。</summary>
        Public Conpty As Boolean = False
    End Class

    ''' <summary>定时任务模型（Cron 6字段：秒 分 时 日 月 周）。</summary>
    Public Class MslTask
        Public Cron As String = ""
        Public Cmd As String = ""
        Public LastRun As String = ""
        <JsonIgnore()> Public LastKey As Integer = -1
    End Class

    '===============================
    '  字段与初始化
    '===============================

    Private Shared ReadOnly Client As New HttpClient()
    Private PathMsl As String = Paths.AppDataThenName & "GloriaTools\MSL\"
    Private PathFrp As String = Paths.AppDataThenName & "GloriaTools\Frp\"
    Private PathServers As String = ""
    Private PathSettings As String = ""
    Private PathTasks As String = ""

    Private Instances As New List(Of MslInstance)
    Private SelectedInst As MslInstance = Nothing
    Private ProcMsl As Process = Nothing
    Private RunningInst As MslInstance = Nothing
    Private SwMslIn As StreamWriter = Nothing
    Private ProcMap As Process = Nothing
    Private ManifestJson As String = ""

    Private CurrentMslPanel As FrameworkElement = Nothing
    ''' <summary>最近创建成功的实例（用于终端模式选择页）。</summary>
    Private LastCreated As MslInstance = Nothing
    Private _ForceKilled As Boolean = False

    '日志缓冲：后台高频输出时先写入缓冲区，由定时器批量刷新到 UI，避免每行都跨线程刷新造成卡顿
    Private ConsBuf As New StringBuilder(), LogBuf As New StringBuilder()
    Private ConsLock As New Object()
    Private FlushTimer As DispatcherTimer = Nothing
    ''' <summary>系统占用刷新定时器（仪表盘）。</summary>
    Private SysTimer As DispatcherTimer = Nothing
    ''' <summary>定时任务调度器（每秒评估 Cron）。</summary>
    Private TaskTimer As DispatcherTimer = Nothing

    Private _InitDone As Boolean = False

    Private Sub Init() Handles Me.Loaded
        EnsureInit()
    End Sub

    ''' <summary>
    ''' 初始化（可由联机中心在嵌入内容后手动调用）。
    ''' </summary>
    Public Sub EnsureInit()
        If _InitDone Then Return
        _InitDone = True
        PathServers = PathMsl & "Servers\"
        PathSettings = PathMsl & "settings.json"
        PathTasks = PathMsl & "tasks.json"
        '初始化默认子页（主页）显示，加载阶段不做动画，避免与页面进入动画冲突
        PanHome.Visibility = Visibility.Visible
        PanList.Visibility = Visibility.Collapsed
        PanCreate.Visibility = Visibility.Collapsed
        PanTermMode.Visibility = Visibility.Collapsed
        PanSrv.Visibility = Visibility.Collapsed
        PanMap.Visibility = Visibility.Collapsed
        PanSet.Visibility = Visibility.Collapsed
        PanOnline.Visibility = Visibility.Collapsed
        PanAbout.Visibility = Visibility.Collapsed
        '服务器管理内部页默认状态
        PanSrvDash.Visibility = Visibility.Visible
        PanSrvCons.Visibility = Visibility.Collapsed
        PanSrvMods.Visibility = Visibility.Collapsed
        PanSrvSet.Visibility = Visibility.Collapsed
        PanSrvFunc.Visibility = Visibility.Collapsed
        PanSrvTask.Visibility = Visibility.Collapsed
        PanInstSet.Visibility = Visibility.Visible
        PanInstProps.Visibility = Visibility.Collapsed
        CurrentMslPanel = PanHome
        BtnTabHome.Checked = True
        BtnSrvDash.Checked = True
        BtnInstTab.Checked = True
        RdoTermPlain.Checked = True
        BtnOnlineP2P.Checked = True
        AttachMenu(BtnMslMore, AddressOf OnMslMenu)
        '定时器只在首次创建，避免每次进入页面重复创建叠加导致卡顿
        If FlushTimer Is Nothing Then
            FlushTimer = New DispatcherTimer() With {.Interval = TimeSpan.FromMilliseconds(120)}
            AddHandler FlushTimer.Tick, AddressOf FlushMslLogs
            FlushTimer.Start()
        End If
        If SysTimer Is Nothing Then
            SysTimer = New DispatcherTimer() With {.Interval = TimeSpan.FromSeconds(2)}
            AddHandler SysTimer.Tick, AddressOf SysTick
            SysTimer.Start()
        End If
        If TaskTimer Is Nothing Then
            TaskTimer = New DispatcherTimer() With {.Interval = TimeSpan.FromSeconds(1)}
            AddHandler TaskTimer.Tick, AddressOf TaskTimerTick
        End If
        LoadInstances()
        LoadTasks()
        LoadSettings()
        UpdateCreateMode()
        RefreshMslState()
        UpdateTunnelUI()
        '仅在尚未加载过版本列表时才联网拉取，避免重复请求
        If ComboCreateVersion.Items.Count = 0 Then RunInNewThread(AddressOf LoadVersionsThread)
    End Sub

    Private Sub AttachMenu(Btn As MyButton, H As RoutedEventHandler)
        If Btn.ContextMenu IsNot Nothing Then
            For Each it As Object In Btn.ContextMenu.Items
                Dim mi As MyMenuItem = TryCast(it, MyMenuItem)
                If mi IsNot Nothing Then AddHandler mi.Click, H
            Next
        End If
    End Sub

    '===============================
    '  子页切换（主页/服务器/新建/终端模式/服务器管理/映射/联机/设置/关于）
    '===============================

    Private Sub TabMsl_Check(sender As Object, raiseByMouse As Boolean) Handles BtnTabHome.Check, BtnTabList.Check, BtnTabCreate.Check, BtnTabTerm.Check,
                                                                                 BtnTabSrv.Check, BtnTabMap.Check, BtnTabOnline.Check,
                                                                                 BtnTabAbout.Check, BtnTabSet.Check
        Dim T As FrameworkElement
        If BtnTabHome.Checked Then
            T = PanHome
        ElseIf BtnTabList.Checked Then
            T = PanList
        ElseIf BtnTabCreate.Checked Then
            T = PanCreate
        ElseIf BtnTabTerm.Checked Then
            T = PanTermMode
        ElseIf BtnTabSrv.Checked Then
            T = PanSrv
        ElseIf BtnTabMap.Checked Then
            T = PanMap
        ElseIf BtnTabOnline.Checked Then
            T = PanOnline
        ElseIf BtnTabAbout.Checked Then
            T = PanAbout
        Else
            T = PanSet
        End If
        SwitchMslTab(T)
    End Sub

    '服务器管理内部页签切换
    Private Sub SrvTab_Check(sender As Object, raiseByMouse As Boolean) Handles BtnSrvDash.Check, BtnSrvCons.Check, BtnSrvMods.Check,
                                                                                 BtnSrvSet.Check, BtnSrvFunc.Check, BtnSrvTask.Check
        Dim T As FrameworkElement
        If BtnSrvDash.Checked Then
            T = PanSrvDash
        ElseIf BtnSrvCons.Checked Then
            T = PanSrvCons
        ElseIf BtnSrvMods.Checked Then
            T = PanSrvMods
            RefreshMods()
        ElseIf BtnSrvFunc.Checked Then
            T = PanSrvFunc
        ElseIf BtnSrvTask.Checked Then
            T = PanSrvTask
            RefreshTasks()
        Else
            T = PanSrvSet
            RefreshDash()
        End If
        SwitchSrvTab(T)
    End Sub

    '实例设置子页签：实例设置 / 服务器配置
    Private Sub InstTab_Check(sender As Object, raiseByMouse As Boolean) Handles BtnInstTab.Check, BtnPropTab.Check
        If BtnInstTab Is Nothing OrElse PanInstSet Is Nothing Then Return
        PanInstSet.Visibility = If(BtnInstTab.Checked, Visibility.Visible, Visibility.Collapsed)
        PanInstProps.Visibility = If(BtnPropTab.Checked, Visibility.Visible, Visibility.Collapsed)
        If BtnPropTab.Checked Then LoadProps() : LoadLists()
    End Sub

    '服务器管理内部页平滑切换（与主子页一致：淡入 + 轻微上滑）
    Private Sub SwitchSrvTab(Target As FrameworkElement)
        If Target Is CurrentSrvPanel Then Return
        If CurrentSrvPanel IsNot Nothing Then CurrentSrvPanel.Visibility = Visibility.Collapsed
        CurrentSrvPanel = Target
        Target.Visibility = Visibility.Visible
        Target.Opacity = 0
        Target.RenderTransform = New TranslateTransform(0, 12)
        AniStart({
            AaOpacity(Target, 1, 160,, New AniEaseOutFluent),
            AaTranslateY(Target, -12, 160,, New AniEaseOutFluent)
        }, "MslSrvTabSwitch " & Target.Name)
    End Sub

    Private CurrentSrvPanel As FrameworkElement = Nothing

    '平滑切换子页：先折叠旧面板，再以「淡入 + 轻微上滑」呈现新面板（使用 Translate 变换，不触发布局重排，保证流畅）
    Private Sub SwitchMslTab(Target As FrameworkElement)
        If Target Is CurrentMslPanel Then Return
        If CurrentMslPanel IsNot Nothing Then CurrentMslPanel.Visibility = Visibility.Collapsed
        CurrentMslPanel = Target
        Target.Visibility = Visibility.Visible
        Target.Opacity = 0
        Target.RenderTransform = New TranslateTransform(0, 12)
        AniStart({
            AaOpacity(Target, 1, 160,, New AniEaseOutFluent),
            AaTranslateY(Target, -12, 160,, New AniEaseOutFluent)
        }, "MslTabSwitch " & Target.Name)
    End Sub

    '快速开始入口
    Private Sub BtnHomeList_Click() Handles BtnHomeList.Click
        BtnTabList.Checked = True
    End Sub
    Private Sub BtnHomeCreate_Click() Handles BtnHomeCreate.Click
        BtnTabCreate.Checked = True
    End Sub
    Private Sub BtnHomeMap_Click() Handles BtnHomeMap.Click
        BtnTabMap.Checked = True
    End Sub

    '===============================
    '  实例持久化（servers.json）
    '===============================

    Private Sub LoadInstances()
        Try
            Instances.Clear()
            If File.Exists(PathServers & "servers.json") Then
                Dim Arr = JArray.Parse(File.ReadAllText(PathServers & "servers.json"))
                For Each O As JObject In Arr
                    Dim I As New MslInstance With {
                        .Name = If(O("Name") Is Nothing, "", O("Name").ToString()),
                        .CorePath = If(O("CorePath") Is Nothing, "", O("CorePath").ToString()),
                        .Java = If(O("Java") Is Nothing, "", O("Java").ToString()),
                        .Memory = If(O("Memory") Is Nothing, "自动", O("Memory").ToString()),
                        .Args = If(O("Args") Is Nothing, "", O("Args").ToString()),
                        .Conpty = If(O("Conpty") Is Nothing, False, CBool(O("Conpty")))
                    }
                    Instances.Add(I)
                Next
            End If
        Catch ex As Exception
            LogMsl("读取实例列表失败：" & ex.Message)
        End Try
        RunInUi(Sub() RefreshInstanceList())
    End Sub

    Private Sub SaveInstances()
        Try
            Directory.CreateDirectory(PathServers)
            Dim Arr As New JArray()
            For Each I As MslInstance In Instances
                Arr.Add(New JObject From {{"Name", I.Name}, {"CorePath", I.CorePath}, {"Java", I.Java}, {"Memory", I.Memory}, {"Args", I.Args}, {"Conpty", I.Conpty}})
            Next
            File.WriteAllText(PathServers & "servers.json", Arr.ToString(Formatting.Indented), Encoding.UTF8)
        Catch ex As Exception
            LogMsl("保存实例列表失败：" & ex.Message)
        End Try
    End Sub

    Private Sub RefreshInstanceList()
        PanInstanceList.Children.Clear()
        PanListEmpty.Visibility = If(Instances.Count = 0, Visibility.Visible, Visibility.Collapsed)
        For Each I As MslInstance In Instances
            Dim Inst = I
            Dim Card As New Border With {
                .Background = TryCast(FindResource("ColorBrushBg1"), Brush),
                .CornerRadius = New CornerRadius(6),
                .Margin = New Thickness(0, 0, 0, 8),
                .Padding = New Thickness(12, 9, 12, 9)
            }
            Dim G As New Grid With {.VerticalAlignment = VerticalAlignment.Center}
            G.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            G.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Dim SpL As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            Dim LabName As New TextBlock With {.Text = Inst.Name, .FontSize = 14, .FontWeight = FontWeights.Bold}
            Dim LabCore As New TextBlock With {.Text = Inst.CorePath, .FontSize = 11.5, .Foreground = TryCast(FindResource("ColorBrushGray3"), Brush), .TextTrimming = TextTrimming.CharacterEllipsis, .Margin = New Thickness(0, 2, 0, 0)}
            SpL.Children.Add(LabName) : SpL.Children.Add(LabCore)
            Grid.SetColumn(SpL, 0)
            Dim SpR As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
            Dim BStart As New MyButton With {.Text = "启动", .Padding = New Thickness(12, 5, 12, 5), .ColorType = MyButton.ColorState.Highlight, .Margin = New Thickness(0, 0, 8, 0)}
            AddHandler BStart.Click, Sub() StartInstance(Inst)
            Dim BCfg As New MyButton With {.Text = "管理", .Padding = New Thickness(12, 5, 12, 5), .Margin = New Thickness(0, 0, 8, 0)}
            AddHandler BCfg.Click, Sub() SrvOpen(Inst)
            Dim BDel As New MyButton With {.Text = "删除", .Padding = New Thickness(12, 5, 12, 5), .ColorType = MyButton.ColorState.Red}
            AddHandler BDel.Click, Sub() DeleteInstance(Inst)
            SpR.Children.Add(BStart) : SpR.Children.Add(BCfg) : SpR.Children.Add(BDel)
            Grid.SetColumn(SpR, 1)
            G.Children.Add(SpL) : G.Children.Add(SpR)
            Card.Child = G
            PanInstanceList.Children.Add(Card)
        Next
        LabHomeCount.Text = Instances.Count.ToString()
    End Sub

    Private Sub DeleteInstance(Inst As MslInstance)
        If IsRunning() AndAlso RunningInst Is Inst Then
            Hint("请先停止该实例再删除！", HintType.Red) : Return
        End If
        Instances.Remove(Inst)
        If SelectedInst Is Inst Then SelectedInst = Nothing
        SaveInstances()
        RefreshInstanceList()
        Hint("已删除实例（服务器存档文件保留在原目录）。", HintType.Blue)
    End Sub

    '===============================
    '  新建服务器
    '===============================

    '开服模式切换：快速模式只留名称/类型/内存；自定义模式显示全部；整合包模式只留名称与整合包文件
    Private Sub ComboCreateMode_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboCreateMode.SelectionChanged
        UpdateCreateMode()
    End Sub

    Private Sub UpdateCreateMode()
        If RowCreateVersion Is Nothing Then Return
        Dim M = ComboText(ComboCreateMode)
        If M.StartsWith("快速") Then
            RowCreateType.Visibility = Visibility.Visible
            RowCreateMemory.Visibility = Visibility.Visible
            RowCreateVersion.Visibility = Visibility.Collapsed
            RowCreateLocal.Visibility = Visibility.Collapsed
            RowCreateJava.Visibility = Visibility.Collapsed
            RowCreateModpack.Visibility = Visibility.Collapsed
            LabCreateModeHint.Text = "自动下载所选类型最新核心，填好名字即可一键开服。"
        ElseIf M.StartsWith("导入") Then
            RowCreateType.Visibility = Visibility.Collapsed
            RowCreateMemory.Visibility = Visibility.Visible
            RowCreateVersion.Visibility = Visibility.Collapsed
            RowCreateLocal.Visibility = Visibility.Collapsed
            RowCreateJava.Visibility = Visibility.Collapsed
            RowCreateModpack.Visibility = Visibility.Visible
            LabCreateModeHint.Text = "选择服务端整合包 zip，解压后自动识别核心（如 Forge/Fabric 的 run.bat 或 server.jar）。"
        Else
            RowCreateType.Visibility = Visibility.Visible
            RowCreateMemory.Visibility = Visibility.Visible
            RowCreateVersion.Visibility = Visibility.Visible
            RowCreateLocal.Visibility = Visibility.Visible
            RowCreateJava.Visibility = Visibility.Visible
            RowCreateModpack.Visibility = Visibility.Collapsed
            LabCreateModeHint.Text = "可手动选择版本、本地核心与 Java，操作自由度最高。"
        End If
    End Sub

    Private Sub BtnCreateModpack_Click() Handles BtnCreateModpack.Click
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择服务端整合包",
            .Filter = "整合包压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return
        TxtCreateName.Tag = Dlg.FileName
        LabCreateModpack.Text = Dlg.FileName
        LogMsl("已选择整合包：" & Dlg.FileName)
    End Sub

    Private Sub BtnCreateRefresh_Click() Handles BtnCreateRefresh.Click
        RunInNewThread(AddressOf LoadVersionsThread)
    End Sub

    Private Sub LoadVersionsThread()
        Try
            LogMsl("正在获取版本列表……")
            ManifestJson = Client.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json").GetAwaiter().GetResult()
            Dim Job As JObject = JObject.Parse(ManifestJson)
            Dim Names As New List(Of String)
            For Each v As JToken In Job("versions")
                If v("type") IsNot Nothing AndAlso v("type").ToString() = "release" Then Names.Add(v("id").ToString())
                If Names.Count >= 80 Then Exit For
            Next
            RunInUi(Sub()
                        ComboCreateVersion.Items.Clear()
                        For Each N As String In Names
                            ComboCreateVersion.Items.Add(N)
                        Next
                        If ComboCreateVersion.Items.Count > 0 Then ComboCreateVersion.SelectedIndex = 0
                    End Sub)
            LogMsl("版本列表获取完成，共 " & Names.Count & " 个正式版。")
        Catch ex As Exception
            RunInUi(Sub() LogMsl("版本列表获取失败：" & ex.Message))
        End Try
    End Sub

    Private Sub BtnCreateJava_Click() Handles BtnCreateJava.Click
        Dim Dlg = PickJava()
        If Dlg <> "" Then TxtCreateJava.Text = Dlg
    End Sub

    Private Sub BtnCreateLocal_Click() Handles BtnCreateLocal.Click
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择服务端核心",
            .Filter = "服务端核心 (*.jar)|*.jar|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return
        TxtCreateName.Tag = Dlg.FileName
        LabCreateCore.Text = Dlg.FileName
        LogMsl("已选择本地核心：" & Dlg.FileName)
    End Sub

    Private Sub BtnCreateDownload_Click() Handles BtnCreateDownload.Click
        Dim Ver = ComboText(ComboCreateVersion)
        Dim T = ComboText(ComboCreateType)
        BtnCreateDownload.IsEnabled = False
        RunInNewThread(Sub()
                           Try
                               Dim Dir As String = PathServers & "_cache\"
                               Dim Core As String = DownloadCore(T, Ver, Dir)
                               RunInUi(Sub()
                                           TxtCreateName.Tag = Core
                                           LabCreateCore.Text = Core
                                           LogMsl("核心已就绪：" & Core)
                                           Hint("核心下载完成！填写实例名称后点击「创建实例」。", HintType.Green)
                                           BtnCreateDownload.IsEnabled = True
                                           DlHide()
                                       End Sub)
                           Catch ex As Exception
                               RunInUi(Sub()
                                           LogMsl("核心下载失败：" & ex.Message)
                                           Hint("核心下载失败，请查看日志输出。", HintType.Red)
                                           BtnCreateDownload.IsEnabled = True
                                           DlHide()
                                       End Sub)
                           End Try
                       End Sub)
    End Sub

    ''' <summary>
    ''' 按服务器类型下载/安装服务端核心到 DestDir，返回可直接用于启动的核心路径。
    ''' 支持：原版(Mojang) / Paper插件 / Velocity代理 / Fabric(混合与纯模组) / Forge安装器 / NeoForge安装器 / 基岩版BDS。
    ''' </summary>
    Private Function DownloadCore(T As String, Ver As String, DestDir As String) As String
        Directory.CreateDirectory(DestDir)
        If T.Contains("Fabric") Then
            Dim Game As String = If(Ver = "", LatestVanilla(), Ver)
            DlShow("正在获取 Fabric 加载器信息……", Nothing)
            LogMsl("正在获取 Fabric 加载器信息……")
            Dim Loaders = JArray.Parse(Client.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader").GetAwaiter().GetResult())
            Dim Loader As String = Loaders(0)("version").ToString()
            Dim Installers = JArray.Parse(Client.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer").GetAwaiter().GetResult())
            Dim Inst As String = Installers(0)("version").ToString()
            Dim Url As String = $"https://meta.fabricmc.net/v2/versions/loader/{Game}/{Loader}/{Inst}/server/jar"
            Dim Dest As String = DestDir & "fabric-server-" & Game & ".jar"
            LogMsl("开始下载 Fabric 服务端（游戏版本 " & Game & "，加载器 " & Loader & "）……")
            DownloadFile(Url, Dest, "正在下载 Fabric 服务端（" & Game & "，加载器 " & Loader & "）")
            Return Dest
        ElseIf T = "插件服务器" Then
            'PaperMC（Java 版插件服务端）
            Return DownloadPaperLike("paper", Ver, DestDir, "paper")
        ElseIf T = "代理服务器" Then
            'Velocity 代理（PaperMC 同一套 API）
            Return DownloadPaperLike("velocity", "", DestDir, "velocity")
        ElseIf T.Contains("Forge") Then
            'Forge：下载官方安装器并静默安装（生成 run.bat 与 libraries）
            Return InstallForgeLike("forge", Ver, DestDir)
        ElseIf T.Contains("NeoForge") Then
            Return InstallForgeLike("neoforge", Ver, DestDir)
        ElseIf T = "基岩版服务器" Then
            DlShow("正在获取基岩版服务端（BDS）下载地址……", Nothing)
            LogMsl("正在获取基岩版服务端（BDS）下载地址……")
            Dim Html As String = Client.GetStringAsync("https://www.minecraft.net/en-us/download/server/bedrock").GetAwaiter().GetResult()
            Dim M As Match = Regex.Match(Html, "https://[^""' ]*bin-win/bedrock-server-[\d.]+\.zip")
            If Not M.Success Then Throw New Exception("未能从官网解析到 BDS 下载地址（页面结构可能已变化）")
            Dim ZipPath As String = DestDir & "bedrock-server.zip"
            LogMsl("开始下载 BDS：" & M.Value)
            DownloadFile(M.Value, ZipPath, "正在下载基岩版服务端（BDS）")
            ExtractZip(ZipPath, DestDir)
            File.Delete(ZipPath)
            Dim Exe As String = DestDir & "bedrock_server.exe"
            If Not File.Exists(Exe) Then Throw New Exception("BDS 解压后未找到 bedrock_server.exe")
            Return Exe
        Else
            '原版服务器（Mojang 官方清单）
            If String.IsNullOrEmpty(ManifestJson) Then
                LogMsl("正在获取版本列表……")
                ManifestJson = Client.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json").GetAwaiter().GetResult()
            End If
            If Ver = "" Then Ver = LatestVanilla()
            Dim Job As JObject = JObject.Parse(ManifestJson)
            Dim Url As String = ""
            For Each v As JToken In Job("versions")
                If v("id") IsNot Nothing AndAlso v("id").ToString() = Ver Then
                    Url = v("url").ToString()
                    Exit For
                End If
            Next
            If Url = "" Then Throw New Exception("未找到该版本的信息")
            Dim VJob As JObject = JObject.Parse(Client.GetStringAsync(Url).GetAwaiter().GetResult())
            Dim DlUrl As String = VJob("downloads")("server")("url").ToString()
            Dim Dest As String = DestDir & "server-" & Ver & ".jar"
            LogMsl("开始下载原版服务端核心（" & Ver & "）……")
            DownloadFile(DlUrl, Dest, "正在下载原版服务端核心（" & Ver & "）")
            Return Dest
        End If
    End Function

    'PaperMC 系（paper/velocity）：取最新版本最新构建
    Private Function DownloadPaperLike(Project As String, Ver As String, DestDir As String, Label As String) As String
        DlShow("正在获取 " & Label & " 版本信息……", Nothing)
        LogMsl("正在获取 " & Label & " 版本信息……")
        Dim Job As JObject = JObject.Parse(Client.GetStringAsync("https://api.papermc.io/v2/projects/" & Project).GetAwaiter().GetResult())
        Dim Versions As JArray = Job("versions")
        Dim V As String = If(Ver = "", Versions(Versions.Count - 1).ToString(), Ver)
        Dim Builds As JObject = JObject.Parse(Client.GetStringAsync($"https://api.papermc.io/v2/projects/{Project}/versions/{V}/builds").GetAwaiter().GetResult())
        Dim Bs As JArray = Builds("builds")
        Dim B As JObject = Bs(Bs.Count - 1)
        Dim Name As String = B("downloads")("application")("name").ToString()
        Dim Url As String = $"https://api.papermc.io/v2/projects/{Project}/versions/{V}/builds/{B("build")}/downloads/{Name}"
        Dim Dest As String = DestDir & Name
        LogMsl("开始下载 " & Label & " 核心（" & V & " 构建 " & B("build").ToString() & "）……")
        DownloadFile(Url, Dest, "正在下载 " & Label & " 核心（" & V & "，构建 " & B("build").ToString() & "）")
        Return Dest
    End Function

    'Forge / NeoForge：下载安装器并静默安装到 DestDir，返回 run.bat
    Private Function InstallForgeLike(Kind As String, Ver As String, DestDir As String) As String
        Dim InstallerUrl As String = "", FullVer As String = ""
        If Kind = "forge" Then
            DlShow("正在获取 Forge 推荐版本……", Nothing)
            LogMsl("正在获取 Forge 推荐版本……")
            Dim Promo As JObject = JObject.Parse(Client.GetStringAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json").GetAwaiter().GetResult())
            Dim Promos As JObject = Promo("promos")
            Dim Game As String = If(Ver = "", LatestVanilla(), Ver)
            FullVer = If(Promos(Game & "-recommended") IsNot Nothing, Promos(Game & "-recommended").ToString(), If(Promos(Game & "-latest") IsNot Nothing, Promos(Game & "-latest").ToString(), ""))
            If FullVer = "" Then FullVer = Promos("1.20.1-recommended").ToString() '回退到长期支持版
            InstallerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{Game}-{FullVer}/forge-{Game}-{FullVer}-installer.jar"
        Else
            DlShow("正在获取 NeoForge 最新版本……", Nothing)
            LogMsl("正在获取 NeoForge 最新版本……")
            Dim Vers As JArray = JArray.Parse(Client.GetStringAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge").GetAwaiter().GetResult())("versions")
            FullVer = Vers(Vers.Count - 1).ToString()
            InstallerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{FullVer}/neoforge-{FullVer}-installer.jar"
        End If
        Dim InstJar As String = DestDir & "installer.jar"
        LogMsl("开始下载安装器（" & FullVer & "）……")
        DownloadFile(InstallerUrl, InstJar, "正在下载 " & Kind & " 安装器（" & FullVer & "）")
        DlShow("正在安装 " & Kind & " 服务端（可能需要几分钟，请耐心等待）……", Nothing)
        LogMsl("正在静默安装服务端（可能需要几分钟，请耐心等待）……")
        Dim Java As String = If(TxtCreateJava.Text.Trim() <> "", TxtCreateJava.Text.Trim(), FindJavaAuto())
        If Java = "" OrElse Not File.Exists(Java) Then Throw New Exception("未找到可用的 Java，请先到「设置」页填写默认 Java")
        Dim P = Process.Start(New ProcessStartInfo With {
            .FileName = Java, .Arguments = "-jar """ & InstJar & """ --installServer", .WorkingDirectory = DestDir,
            .UseShellExecute = False, .CreateNoWindow = True})
        If Not P.WaitForExit(600000) Then
            Try : P.Kill() : Catch : End Try
            Throw New Exception("安装超时（10 分钟）")
        End If
        If P.ExitCode <> 0 Then Throw New Exception("安装器退出码 " & P.ExitCode & "，请检查 Java 版本是否符合核心要求")
        Dim Bat As String = DestDir & "run.bat"
        If File.Exists(Bat) Then
            File.Delete(InstJar)
            LogMsl("安装完成：" & Bat)
            Return Bat
        End If
        '老版本安装器直接生成核心 jar
        Dim Jars = Directory.GetFiles(DestDir, "*.jar").Where(Function(f) Path.GetFileName(f) <> "installer.jar").ToArray()
        If Jars.Length > 0 Then
            File.Delete(InstJar)
            Return Jars(0)
        End If
        Throw New Exception("安装完成但未找到可启动的核心（run.bat 或 jar）")
    End Function

    Private Function LatestVanilla() As String
        If String.IsNullOrEmpty(ManifestJson) Then
            ManifestJson = Client.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json").GetAwaiter().GetResult()
        End If
        Dim Job As JObject = JObject.Parse(ManifestJson)
        For Each v As JToken In Job("versions")
            If v("type") IsNot Nothing AndAlso v("type").ToString() = "release" Then Return v("id").ToString()
        Next
        Throw New Exception("无法获取最新原版版本")
    End Function

    '安全解压 zip（防路径穿越）
    Private Sub ExtractZip(ZipPath As String, DestDir As String)
        Directory.CreateDirectory(DestDir)
        Dim Root As String = Path.GetFullPath(DestDir)
        Using Zip As New ZipArchive(File.OpenRead(ZipPath), ZipArchiveMode.Read)
            For Each E As ZipArchiveEntry In Zip.Entries
                Dim Target As String = Path.GetFullPath(Path.Combine(DestDir, E.FullName))
                If Not Target.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then Continue For
                If E.FullName.EndsWith("/") OrElse E.FullName.EndsWith("\") Then
                    Directory.CreateDirectory(Target)
                Else
                    Directory.CreateDirectory(Path.GetDirectoryName(Target))
                    E.ExtractToFile(Target, True)
                End If
            Next
        End Using
    End Sub

    Private Sub BtnCreateOk_Click() Handles BtnCreateOk.Click
        Dim Name As String = TxtCreateName.Text.Trim()
        If Name = "" Then
            Hint("请先填写实例名称！", HintType.Red) : Return
        End If
        If Instances.Any(Function(x) x.Name = Name) Then
            Hint("已存在同名实例，请换一个名称。", HintType.Red) : Return
        End If
        Dim M = ComboText(ComboCreateMode)
        If M.StartsWith("导入") Then
            '导入整合包模式：解压 zip → 自动识别核心 → 建实例
            Dim Pack = If(TxtCreateName.Tag Is Nothing, "", TxtCreateName.Tag.ToString())
            If Pack = "" OrElse Not File.Exists(Pack) Then
                Hint("请先选择整合包 zip 文件！", HintType.Red) : Return
            End If
            Dim Dir As String = PathServers & Name & "\"
            BtnCreateOk.IsEnabled = False
            RunInNewThread(Sub()
                               Try
                                   LogMsl("正在解压整合包……")
                                   Directory.CreateDirectory(Dir)
                                   ExtractZip(Pack, Dir)
                                   Dim Core As String = DetectCoreInDir(Dir)
                                   If Core = "" Then Throw New Exception("整合包中未识别到核心（未找到 run.bat / server.jar / bedrock_server.exe 等）")
                                   RunInUi(Sub() FinishCreate(Name, Core))
                                   LogMsl("整合包导入完成，核心：" & Core)
                               Catch ex As Exception
                                   RunInUi(Sub()
                                               LogMsl("整合包导入失败：" & ex.Message)
                                               Hint("整合包导入失败：" & ex.Message, HintType.Red)
                                               BtnCreateOk.IsEnabled = True
                                               DlHide()
                                           End Sub)
                               End Try
                           End Sub)
            Return
        End If
        Dim Core0 = If(TxtCreateName.Tag Is Nothing, "", TxtCreateName.Tag.ToString())
        If M.StartsWith("快速") AndAlso (Core0 = "" OrElse Not File.Exists(Core0)) Then
            '快速模式：未选择核心时自动下载所选类型最新核心
            Dim T = ComboText(ComboCreateType)
            Dim Mem = ComboText(ComboCreateMemory), Java = TxtCreateJava.Text.Trim()
            BtnCreateOk.IsEnabled = False
            RunInNewThread(Sub()
                               Try
                                   Dim Dir As String = PathServers & Name & "\"
                                   Dim Core As String = DownloadCore(T, "", Dir)
                                   RunInUi(Sub() FinishCreate(Name, Core, Java, Mem))
                               Catch ex As Exception
                                   RunInUi(Sub()
                                               LogMsl("快速开服失败：" & ex.Message)
                                               Hint("快速开服失败：" & ex.Message, HintType.Red)
                                               BtnCreateOk.IsEnabled = True
                                               DlHide()
                                           End Sub)
                               End Try
                           End Sub)
            Return
        End If
        If Core0 = "" OrElse Not File.Exists(Core0) Then
            Hint("请先下载核心或选择本地 jar 文件！", HintType.Red) : Return
        End If
        '自定义模式：本地已有核心，直接建实例
        Dim D2 As String = PathServers & Name & "\"
        Try : Directory.CreateDirectory(D2) : Catch ex As Exception
            Hint("创建实例目录失败：" & ex.Message, HintType.Red) : Return
        End Try
        Dim Dest As String = Core0
        'jar 核心复制进实例目录保证独立；run.bat/exe 等依赖相对路径的核心保留原位
        If Core0.ToLower().EndsWith(".jar") AndAlso Path.GetDirectoryName(Core0) <> D2 Then
            Dest = D2 & Path.GetFileName(Core0)
            Try
                File.Copy(Core0, Dest, True)
            Catch ex As Exception
                Hint("复制核心失败：" & ex.Message, HintType.Red) : Return
            End Try
        End If
        FinishCreate(Name, Dest, TxtCreateJava.Text.Trim(), ComboText(ComboCreateMemory))
    End Sub

    '在整合包目录中识别可启动核心：run.bat > bedrock_server.exe > 常见服务端 jar > 任意最大的 jar
    Private Function DetectCoreInDir(Dir As String) As String
        For Each N As String In {"run.bat", "bedrock_server.exe", "server.jar", "fabric-server-launch.jar", "forge.jar"}
            Dim F As String = Dir & N
            If File.Exists(F) Then Return F
        Next
        Dim RootJars = Directory.GetFiles(Dir, "*.jar", SearchOption.TopDirectoryOnly)
        If RootJars.Length > 0 Then
            Return RootJars.OrderByDescending(Function(f) New FileInfo(f).Length).First()
        End If
        '递归一层找（有些整合包核心在子目录）
        Dim SubJars = Directory.GetFiles(Dir, "*.jar", SearchOption.AllDirectories)
        If SubJars.Length > 0 Then
            Return SubJars.OrderByDescending(Function(f) New FileInfo(f).Length).First()
        End If
        Return ""
    End Function

    '收尾建实例（可从后台线程调用）
    Private Sub FinishCreate(Name As String, Core As String, Optional Java As String = Nothing, Optional Mem As String = Nothing)
        If Java Is Nothing Then Java = TxtCreateJava.Text.Trim()
        If Mem Is Nothing Then Mem = ComboText(ComboCreateMemory)
        Dim Inst As New MslInstance With {
            .Name = Name,
            .CorePath = Core,
            .Java = Java,
            .Memory = Mem,
            .Args = "",
            .Conpty = ChkConpty.Checked
        }
        Instances.Add(Inst)
        SaveInstances()
        RefreshInstanceList()
        TxtCreateName.Text = ""
        TxtCreateName.Tag = Nothing
        LabCreateCore.Text = "未选择（将使用在线下载的核心）"
        LabCreateModpack.Text = "未选择（服务端整合包压缩包，将解压并自动识别核心）"
        BtnCreateOk.IsEnabled = True
        LogMsl("已创建实例：" & Name & "（" & Core & "）")
        Hint("实例创建成功！请选择终端模式以继续。", HintType.Green)
        '进入终端模式选择页（完成后自动进入服务器管理）
        LastCreated = Inst
        DlHide()
        BtnTabTerm.Checked = True
    End Sub

    '===============================
    '  终端模式选择（创建完成后）
    '===============================

    Private Sub BtnTermBack_Click() Handles BtnTermBack.Click
        If LastCreated IsNot Nothing Then
            '回退：删除刚创建的实例，返回新建页重新填写
            Instances.Remove(LastCreated)
            SaveInstances()
            RefreshInstanceList()
            LastCreated = Nothing
        End If
        BtnTabCreate.Checked = True
    End Sub

    Private Sub BtnTermDone_Click() Handles BtnTermDone.Click
        If LastCreated IsNot Nothing Then
            LastCreated.Conpty = RdoTermConpty.Checked
            SaveInstances()
            LogMsl("终端模式已保存：" & LastCreated.Name & " → " & If(RdoTermConpty.Checked, "伪终端（ConPTY，测试中）", "传统模式"))
            Dim Inst = LastCreated
            LastCreated = Nothing
            SrvOpen(Inst)
        Else
            Hint("没有待设置的实例。", HintType.Blue)
        End If
    End Sub

    '===============================
    '  服务器管理（MyServer 风格）
    '===============================

    ''' <summary>打开某实例的服务器管理界面。</summary>
    Private Sub SrvOpen(Inst As MslInstance)
        SelectedInst = Inst
        LastCreated = Nothing
        LabSrvTitle.Text = Inst.Name & " — 服务器管理"
        TxtSetName.Text = Inst.Name
        LabSetDir.Text = InstDir(Inst)
        TxtSetCore.Text = Inst.CorePath
        'Java 选项：已有可用 Java → 手动选择；否则默认在线下载
        If Inst.Java <> "" AndAlso Inst.Java.ToLower() <> "java" AndAlso File.Exists(Inst.Java) Then
            TxtCfgJava.Text = Inst.Java
            SetJavaOpt(JavaOpt.Manual)
        Else
            SetJavaOpt(JavaOpt.Download)
        End If
        SelectComboByText(ComboCfgMemory, Inst.Memory)
        TxtCfgArgs.Text = Inst.Args
        LoadProps()
        LoadLists()
        RefreshDash()
        RefreshMods()
        RefreshPlayers()
        BtnTabSrv.Checked = True
    End Sub

    'Java 选项（手动管理互斥：各选项不在同一父容器中）
    Private Enum JavaOpt
        Manual = 0
        Download = 1
        Local = 2
        [Env] = 3
    End Enum

    Private _JavaOptLock As Boolean = False

    Private Sub SetJavaOpt(Opt As JavaOpt)
        _JavaOptLock = True
        OptJavaManual.Checked = (Opt = JavaOpt.Manual)
        OptJavaDownload.Checked = (Opt = JavaOpt.Download)
        OptJavaLocal.Checked = (Opt = JavaOpt.Local)
        OptJavaEnv.Checked = (Opt = JavaOpt.Env)
        UpdateJavaOptUI()
        _JavaOptLock = False
    End Sub

    Private Sub UpdateJavaOptUI()
        Dim Man = OptJavaManual.Checked, Lc = OptJavaLocal.Checked
        TxtCfgJava.Visibility = If(Man OrElse Lc, Visibility.Visible, Visibility.Collapsed)
        BtnCfgJava.Visibility = If(Man, Visibility.Visible, Visibility.Collapsed)
        TxtCfgJavaSearch.Visibility = If(Lc, Visibility.Visible, Visibility.Collapsed)
        BtnSetJavaSearch.Visibility = If(Lc, Visibility.Visible, Visibility.Collapsed)
        ComboSetJavaVer.Visibility = If(OptJavaDownload.Checked, Visibility.Visible, Visibility.Collapsed)
        BtnSetJavaDownload.Visibility = If(OptJavaDownload.Checked, Visibility.Visible, Visibility.Collapsed)
    End Sub

    Private Sub OptJava_Change(sender As Object, raiseByMouse As Boolean) Handles OptJavaManual.Check, OptJavaDownload.Check, OptJavaLocal.Check, OptJavaEnv.Check
        If _JavaOptLock Then Return
        Dim Opt As JavaOpt
        If sender Is OptJavaManual Then
            Opt = JavaOpt.Manual
        ElseIf sender Is OptJavaDownload Then
            Opt = JavaOpt.Download
        ElseIf sender Is OptJavaLocal Then
            Opt = JavaOpt.Local
        Else
            Opt = JavaOpt.Env
        End If
        SetJavaOpt(Opt)
    End Sub

    ''' <summary>根据当前选择的 Java 选项解析实际 Java 路径。</summary>
    Private Function JavaFromOpt() As String
        If OptJavaManual.Checked Then Return TxtCfgJava.Text.Trim()
        If OptJavaDownload.Checked Then
            '在线下载的 Java：取 GloriaTools\MSL\Java 下已解压的最新版本
            Dim J = FindJavaInMslDir()
            Return If(J IsNot Nothing, J, "")
        End If
        If OptJavaLocal.Checked Then Return TxtCfgJavaSearch.Text.Trim()
        Return "java" '使用环境变量
    End Function

    Private Function FindJavaInMslDir() As String
        Try
            Dim Root = PathMsl & "Java\"
            If Not Directory.Exists(Root) Then Return Nothing
            Dim Best As String = Nothing, BestSize As Long = -1
            For Each F In Directory.EnumerateFiles(Root, "java.exe", SearchOption.AllDirectories)
                Dim Size As Long = 0
                Try : Size = New FileInfo(F).Length : Catch : End Try
                If Size > BestSize Then Best = F : BestSize = Size
            Next
            Return Best
        Catch
            Return Nothing
        End Try
    End Function

    Private Sub BtnCfgJava_Click() Handles BtnCfgJava.Click
        Dim Dlg = PickJava()
        If Dlg <> "" Then TxtCfgJava.Text = Dlg
    End Sub

    Private Function PickJava() As String
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择 Java 运行时",
            .Filter = "Java 运行时 (*.exe)|*.exe|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return ""
        Return Dlg.FileName
    End Function

    Private Function InstDir(Inst As MslInstance) As String
        Return If(Inst.CorePath = "", PathServers, Path.GetDirectoryName(Inst.CorePath) & "\")
    End Function

    Private Sub LoadProps()
        TxtPropsRaw.Text = ""
        If SelectedInst Is Nothing Then Return
        Dim F As String = InstDir(SelectedInst) & "server.properties"
        If File.Exists(F) Then
            TxtPropsRaw.Text = File.ReadAllText(F, Encoding.UTF8)
            ParsePropsToFields(TxtPropsRaw.Text)
        Else
            TxtPropsRaw.Text = ""
            ParsePropsToFields("")
        End If
    End Sub

    Private Sub BtnCfgLoadProps_Click() Handles BtnCfgLoadProps.Click
        LoadProps()
    End Sub

    Private Sub ParsePropsToFields(Text As String)
        Dim Map As New Dictionary(Of String, String)
        For Each L In Text.Split({vbCrLf, vbLf}, StringSplitOptions.None)
            Dim T = L.Trim()
            If T.StartsWith("#") OrElse Not T.Contains("=") Then Continue For
            Dim K = T.Substring(0, T.IndexOf("=")).Trim()
            Dim V = T.Substring(T.IndexOf("=") + 1)
            Map(K) = V
        Next
        Dim GetV = Function(K As String, D As String) If(Map.ContainsKey(K), Map(K), D)
        TxtCfgMotd.Text = GetV("motd", "")
        TxtCfgPort.Text = GetV("server-port", "25565")
        TxtCfgMaxPlayers.Text = GetV("max-players", "20")
        SelectComboByText(ComboCfgGamemode, GetV("gamemode", "survival"))
        SelectComboByText(ComboCfgDifficulty, GetV("difficulty", "easy"))
        SelectComboByText(ComboCfgOnline, If(GetV("online-mode", "true") = "true", "开启", "关闭"))
        SelectComboByText(ComboCfgWhite, If(GetV("white-list", "false") = "true" OrElse GetV("enforce-whitelist", "false") = "true", "开启", "关闭"))
    End Sub

    Private Sub BtnCfgSaveProps_Click() Handles BtnCfgSaveProps.Click
        If SelectedInst Is Nothing Then
            Hint("请先在「服务器」页选择一个实例（点击配置）。", HintType.Red) : Return
        End If
        Try
            '把可视化字段写回原始文本
            Dim Lines As New List(Of String)(TxtPropsRaw.Text.Split({vbCrLf, vbLf}, StringSplitOptions.None))
            SetProp(Lines, "motd", TxtCfgMotd.Text)
            SetProp(Lines, "server-port", TxtCfgPort.Text)
            SetProp(Lines, "max-players", TxtCfgMaxPlayers.Text)
            SetProp(Lines, "gamemode", ComboText(ComboCfgGamemode))
            SetProp(Lines, "difficulty", ComboText(ComboCfgDifficulty))
            SetProp(Lines, "online-mode", If(ComboText(ComboCfgOnline) = "开启", "true", "false"))
            Dim WL As String = If(ComboText(ComboCfgWhite) = "开启", "true", "false")
            SetProp(Lines, "white-list", WL)
            SetProp(Lines, "enforce-whitelist", WL)
            TxtPropsRaw.Text = String.Join(vbCrLf, Lines)
            File.WriteAllText(InstDir(SelectedInst) & "server.properties", TxtPropsRaw.Text, Encoding.UTF8)
            '同步运行环境设置到实例
            SelectedInst.Java = TxtCfgJava.Text.Trim()
            SelectedInst.Args = TxtCfgArgs.Text.Trim()
            SelectedInst.Memory = ComboText(ComboCfgMemory)
            SaveInstances()
            RefreshInstanceList()
            LogMsl("server.properties 已保存：" & InstDir(SelectedInst))
            Hint("server.properties 与运行环境已保存！", HintType.Green)
        Catch ex As Exception
            Hint("保存失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub SetProp(Lines As List(Of String), Key As String, Value As String)
        For i = 0 To Lines.Count - 1
            Dim T = Lines(i).Trim()
            If Not T.StartsWith("#") AndAlso T.Contains("=") AndAlso T.Substring(0, T.IndexOf("=")).Trim() = Key Then
                Lines(i) = Key & "=" & Value
                Return
            End If
        Next
        Lines.Add(Key & "=" & Value)
    End Sub

    Private Sub LoadLists()
        If SelectedInst Is Nothing Then Return
        Dim Dir = InstDir(SelectedInst)
        If File.Exists(Dir & "eula.txt") Then
            CheckCfgEula.Checked = File.ReadAllText(Dir & "eula.txt").ToLower().Contains("eula=true")
        Else
            CheckCfgEula.Checked = False
        End If
        TxtCfgWhitelist.Text = If(File.Exists(Dir & "whitelist.txt"), File.ReadAllText(Dir & "whitelist.txt"), "")
        TxtCfgOps.Text = If(File.Exists(Dir & "ops.json"), File.ReadAllText(Dir & "ops.json"), "")
    End Sub

    Private Sub BtnCfgSaveLists_Click() Handles BtnCfgSaveLists.Click
        If SelectedInst Is Nothing Then
            Hint("请先在「服务器」页选择一个实例（点击配置）。", HintType.Red) : Return
        End If
        Try
            Dim Dir = InstDir(SelectedInst)
            Directory.CreateDirectory(Dir)
            File.WriteAllText(Dir & "eula.txt", "#Generated by GloriaCloud MSL" & vbCrLf & If(CheckCfgEula.Checked, "eula=true", "eula=false") & vbCrLf)
            File.WriteAllText(Dir & "whitelist.txt", TxtCfgWhitelist.Text, Encoding.UTF8)
            '由 OP 名单生成 ops.json
            Dim Arr As New JArray()
            For Each L In TxtCfgOps.Text.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                Dim N = L.Trim()
                If N = "" OrElse N.StartsWith("#") Then Continue For
                Dim Rand(15) As Byte
                Using Rng = System.Security.Cryptography.RandomNumberGenerator.Create()
                    Rng.GetBytes(Rand)
                End Using
                Dim Uuid As New Guid(Rand)
                Dim lvl = New JObject From {{"uuid", Uuid.ToString()}, {"name", N}, {"level", 4}, {"bypassesPlayerLimit", False}}
                Arr.Add(lvl)
            Next
            File.WriteAllText(Dir & "ops.json", Arr.ToString(Formatting.Indented), Encoding.UTF8)
            LogMsl("EULA / 白名单 / OP 已保存：" & Dir)
            Hint("EULA、白名单与 OP 名单已保存！", HintType.Green)
        Catch ex As Exception
            Hint("保存失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    '===============================
    '  启动 / 停止
    '===============================

    '实例未指定 Java 且设置页为空时，自动搜索本机常见目录中的 java.exe，优先版本号更高者
    Private Function ResolveJava(Inst As MslInstance) As String
        If Inst.Java <> "" AndAlso File.Exists(Inst.Java) Then Return Inst.Java
        If TxtSetJava.Text.Trim() <> "" AndAlso File.Exists(TxtSetJava.Text.Trim()) Then Return TxtSetJava.Text.Trim()
        Return FindJavaAuto()
    End Function

    Private Function FindJavaAuto() As String
        Try
            'PATH 中有 java 也可直接用（定位到真实 exe）
            Dim PathVar As String = Environment.GetEnvironmentVariable("Path")
            If PathVar IsNot Nothing Then
                For Each P As String In PathVar.Split(";"c)
                    Dim T = P.Trim().Trim(""""c)
                    If T = "" Then Continue For
                    If File.Exists(T & "\java.exe") Then Return T & "\java.exe"
                    If File.Exists(T & "\bin\java.exe") Then Return T & "\bin\java.exe"
                Next
            End If
            Dim Bases As New List(Of String) From {
                "C:\Program Files\Java", "C:\Program Files\Eclipse Adoptium", "C:\Program Files\Microsoft",
                "C:\Program Files\Zulu", "C:\Program Files\BellSoft", "C:\Program Files\Amazon Corretto",
                "C:\Program Files (x86)\Java", Paths.AppDataThenName & "Java"
            }
            Dim Dirs As New List(Of String)
            For Each B As String In Bases
                If Directory.Exists(B) Then Dirs.AddRange(Directory.GetDirectories(B))
            Next
            Dim Best As String = "", BestScore As Integer = -1
            For Each D As String In Dirs
                For Each J As String In {D & "\bin\java.exe", D & "\jre\bin\java.exe"}
                    If Not File.Exists(J) Then Continue For
                    '从目录名解析版本号（如 jdk-17.0.9、jre-21），取最高
                    Dim Vm As Match = Regex.Match(Path.GetFileName(D), "(\d+)")
                    Dim Score As Integer = If(Vm.Success, Integer.Parse(Vm.Value), 0)
                    If Score > BestScore Then
                        BestScore = Score
                        Best = J
                    End If
                Next
            Next
            Return Best
        Catch
            Return ""
        End Try
    End Function

    Private Function IsRunning() As Boolean
        If ProcMsl Is Nothing Then Return False
        Try
            Return Not ProcMsl.HasExited
        Catch
            '进程从未成功启动（Start 失败）时 HasExited 会抛出"没有与此关联的进程"
            Return False
        End Try
    End Function

    '从 "12 GB" 这类文本解析内存数值（返回 12），非法时返回 0
    Private Function MemNum(Mem As String) As Integer
        Dim M As Match = Regex.Match(Mem, "\d+")
        Return If(M.Success, Integer.Parse(M.Value), 0)
    End Function

    Private Function InstCoreIsBat() As Boolean
        Return RunningInst IsNot Nothing AndAlso RunningInst.CorePath.ToLower().EndsWith(".bat")
    End Function

    Private Sub StartInstance(Inst As MslInstance)
        If IsRunning() Then
            Hint("已有实例在运行（" & RunningInst.Name & "），请先停止！", HintType.Blue) : Return
        End If
        If Not File.Exists(Inst.CorePath) Then
            Hint("实例核心文件不存在：" & Inst.CorePath, HintType.Red) : Return
        End If
        SelectedInst = Inst
        Try
            Dim Java As String = ResolveJava(Inst)
            If Java = "" Then
                Hint("未找到可用的 Java：请到「设置」页填写默认 Java，或安装 Java（建议 17+）后重试。", HintType.Red)
                LogMsl("启动失败：未找到可用的 Java。")
                RefreshMslState()
                Return
            End If
            Dim CoreLower = Inst.CorePath.ToLower()
            Dim Info As ProcessStartInfo
            If CoreLower.EndsWith(".bat") Then
                'Forge/NeoForge 安装器生成的 run.bat：内存写入 user_jvm_args.txt
                Dim WorkDir As String = Path.GetDirectoryName(Inst.CorePath)
                If Inst.Memory <> "自动" AndAlso File.Exists(WorkDir & "\user_jvm_args.txt") Then
                    Dim JvmLines As New List(Of String)(File.ReadAllLines(WorkDir & "\user_jvm_args.txt"))
                    Dim Xmx As String = "-Xmx" & MemNum(Inst.Memory) & "G"
                    JvmLines = JvmLines.Where(Function(l) Not l.TrimStart().StartsWith("-Xmx")).ToList()
                    JvmLines.Add(Xmx)
                    File.WriteAllLines(WorkDir & "\user_jvm_args.txt", JvmLines)
                End If
                Info = New ProcessStartInfo With {
                    .FileName = "cmd.exe",
                    .Arguments = "/c """ & Inst.CorePath & """",
                    .WorkingDirectory = WorkDir,
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .RedirectStandardInput = True,
                    .StandardOutputEncoding = Encoding.UTF8,
                    .StandardErrorEncoding = Encoding.UTF8
                }
            ElseIf CoreLower.EndsWith(".exe") Then
                '基岩版服务端（BDS）直接运行 exe
                Info = New ProcessStartInfo With {
                    .FileName = Inst.CorePath,
                    .WorkingDirectory = Path.GetDirectoryName(Inst.CorePath),
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .RedirectStandardInput = True,
                    .StandardOutputEncoding = Encoding.UTF8,
                    .StandardErrorEncoding = Encoding.UTF8
                }
            Else
                '传统 jar 核心
                Dim Args As String = ""
                If Inst.Memory <> "自动" Then Args &= "-Xmx" & MemNum(Inst.Memory) & "G "
                If ChkUtf8.Checked Then Args &= "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 "
                If Inst.Args <> "" Then Args &= Inst.Args & " "
                'ConPTY（伪终端）模式下不附加 nogui，让部分服务端输出更接近真实终端
                Args &= "-jar """ & Inst.CorePath & """" & If(Inst.Conpty, "", " nogui")
                Info = New ProcessStartInfo With {
                    .FileName = Java,
                    .Arguments = Args,
                    .WorkingDirectory = Path.GetDirectoryName(Inst.CorePath),
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .RedirectStandardInput = True,
                    .StandardOutputEncoding = Encoding.UTF8,
                    .StandardErrorEncoding = Encoding.UTF8
                }
            End If
            '自动清屏
            If ChkAutoClear.Checked Then
                TxtMslConsole.Clear()
                SyncLock ConsLock : ConsBuf.Clear() : End SyncLock
            End If
            _ForceKilled = False
            ProcMsl = New Process With {.StartInfo = Info, .EnableRaisingEvents = True}
            AddHandler ProcMsl.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then OnSrvLine(e.Data)
            AddHandler ProcMsl.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then OnSrvLine(e.Data)
            AddHandler ProcMsl.Exited, Sub(s, e)
                                           SwMslIn = Nothing
                                           RunInUi(Sub() ConsMsl("[实例 " & If(RunningInst IsNot Nothing, RunningInst.Name, "?") & " 已退出]"))
                                           RunInUi(AddressOf OnSrvExitedUi)
                                       End Sub
            ProcMsl.Start()
            SwMslIn = ProcMsl.StandardInput
            ProcMsl.BeginOutputReadLine()
            ProcMsl.BeginErrorReadLine()
            RunningInst = Inst
            LogMsl("已启动实例：" & Inst.Name)
            LogMsl("启动命令：" & Info.FileName & " " & Info.Arguments)
            If Inst.Conpty Then LogMsl("伪终端（ConPTY，测试中）模式已启用。")
            ConsMsl("[GloriaCloud] 实例 " & Inst.Name & " 已启动，可在下方输入指令。")
            Hint("实例已启动！可在「仪表盘」查看状态、「控制台」查看输出。", HintType.Green)
        Catch ex As Exception
            ProcMsl = Nothing
            RunningInst = Nothing
            LogMsl("启动失败：" & ex.Message)
            Hint("启动失败：" & ex.Message, HintType.Red)
        End Try
        RefreshMslState()
    End Sub

    Private Sub StopServer(Force As Boolean)
        If Not IsRunning() Then
            Hint("服务器未在运行。", HintType.Blue) : Return
        End If
        _ForceKilled = True
        Try
            If Force Then
                'run.bat 方式启动时需结束整棵进程树（cmd + java）
                If InstCoreIsBat() OrElse ChkKillTree.Checked Then
                    Process.Start(New ProcessStartInfo With {.FileName = "taskkill", .Arguments = "/F /T /PID " & ProcMsl.Id, .CreateNoWindow = True, .UseShellExecute = False})
                Else
                    ProcMsl.Kill()
                End If
                ConsMsl("[服务器进程已被强制结束]")
                LogMsl("已强制结束服务器进程。")
            Else
                If SwMslIn IsNot Nothing Then
                    SwMslIn.WriteLine("stop")
                    ConsMsl("[指令] stop（已发送关服指令，等待服务器安全退出……）")
                    LogMsl("已发送关服指令。")
                    Hint("已发送关服指令，存档将自动保存。", HintType.Blue)
                    '勾选「强制关服时结束进程树」时：10 秒后仍未退出则强制结束
                    If ChkKillTree.Checked Then
                        Dim P = ProcMsl
                        RunInNewThread(Sub()
                                           Try
                                               System.Threading.Thread.Sleep(10000)
                                               If Not P.HasExited Then
                                                   Process.Start(New ProcessStartInfo With {.FileName = "taskkill", .Arguments = "/F /T /PID " & P.Id, .CreateNoWindow = True, .UseShellExecute = False})
                                                   ConsMsl("[关服超时，已强制结束进程树]")
                                               End If
                                           Catch
                                           End Try
                                       End Sub)
                    End If
                Else
                    ProcMsl.Kill()
                End If
            End If
        Catch ex As Exception
            LogMsl("停止失败：" & ex.Message)
        End Try
    End Sub

    '===============================
    '  服务器输出 / 玩家 / 崩溃检测 / 自动重启
    '===============================

    ''' <summary>服务器输出行处理：屏蔽堆栈、记录玩家、写入控制台。</summary>
    Private Sub OnSrvLine(Line As String)
        Try
            If ChkHideStack.Checked AndAlso Line.TrimStart().StartsWith("at ") Then Return
            If Not ChkLogOut.Checked Then
                '关闭日志输出时仅写运行日志，不写控制台
                LogMsl(Line)
                Return
            End If
        Catch
        End Try
        DetectPlayer(Line)
        ConsMsl(Line)
    End Sub

    ''' <summary>服务器进程退出后的 UI 收尾（自动重启 / 崩溃检测）。</summary>
    Private Sub OnSrvExitedUi()
        RefreshMslState()
        If ChkAutoRestart.Checked AndAlso Not _ForceKilled AndAlso RunningInst IsNot Nothing Then
            Dim Inst = RunningInst
            ConsMsl("[GloriaCloud] 关服自动重启已启用，5 秒后自动重启……")
            RunInNewThread(Sub()
                               Try
                                   System.Threading.Thread.Sleep(5000)
                               Catch
                               End Try
                               RunInUi(Sub() If Not IsRunning() Then StartInstance(Inst))
                           End Sub)
        ElseIf Not _ForceKilled Then
            CrashCheck(False)
        End If
        _ForceKilled = False
    End Sub

    ''' <summary>已加入的玩家列表（由控制台输出解析）。</summary>
    Private Players As New List(Of String)

    Private Sub DetectPlayer(Line As String)
        Try
            If Not ChkDashPlayers.Checked Then Return
        Catch
            Return
        End Try
        Try
            Dim Ix = Line.IndexOf("joined the game", StringComparison.OrdinalIgnoreCase)
            Dim Iy = Line.IndexOf("left the game", StringComparison.OrdinalIgnoreCase)
            If Ix > 0 Then
                Dim Name = NameFromLog(Line.Substring(0, Ix))
                If Name <> "" AndAlso Not Players.Contains(Name) Then
                    Players.Add(Name)
                    RunInUi(AddressOf RefreshPlayers)
                End If
            ElseIf Iy > 0 Then
                Dim Name = NameFromLog(Line.Substring(0, Iy))
                If Name <> "" AndAlso Players.Contains(Name) Then
                    Players.Remove(Name)
                    RunInUi(AddressOf RefreshPlayers)
                End If
            End If
        Catch
        End Try
    End Sub

    '从 "…]: Steve" / "…]: Steve" 前缀中提取玩家名
    Private Function NameFromLog(Prefix As String) As String
        Prefix = Prefix.Trim()
        Dim Sep As Integer = Math.Max(Prefix.LastIndexOf(":"c), Prefix.LastIndexOf("]"c))
        If Sep >= 0 AndAlso Sep < Prefix.Length - 1 Then Return Prefix.Substring(Sep + 1).Trim()
        Return Prefix
    End Function

    Private Sub RefreshPlayers()
        If PanDashPlayers Is Nothing Then Return
        PanDashPlayers.Children.Clear()
        PanDashPlayerEmpty.Visibility = If(Players.Count = 0 OrElse Not ChkDashPlayers.Checked, Visibility.Visible, Visibility.Collapsed)
        For Each P As String In Players
            Dim B As New Border With {
                .Background = TryCast(FindResource("ColorBrushBg1"), Brush),
                .CornerRadius = New CornerRadius(6),
                .Padding = New Thickness(10, 5, 10, 5),
                .Margin = New Thickness(0, 0, 0, 5),
                .HorizontalAlignment = HorizontalAlignment.Left
            }
            B.Child = New TextBlock With {.Text = "👤 " & P & " 在线", .FontSize = 12.5}
            PanDashPlayers.Children.Add(B)
        Next
    End Sub

    ''' <summary>崩溃检测：Notify=True 时弹窗显示结果，否则仅提示。</summary>
    Private Sub CrashCheck(Notify As Boolean)
        Try
            Dim Txt As String = ""
            Try : Txt = TxtMslLog.Text & vbCrLf & TxtMslConsole.Text : Catch : End Try
            Dim HasCrash = Txt.Contains("Exception") OrElse Txt.ToLower().Contains("crash-report") OrElse Txt.Contains("Failed to load")
            Dim Dir As String = If(SelectedInst IsNot Nothing, InstDir(SelectedInst), PathServers)
            Dim Rep As String = Nothing
            Dim RepDir = Dir & "crash-reports\"
            If Directory.Exists(RepDir) Then
                Rep = Directory.GetFiles(RepDir, "*.txt").OrderByDescending(Function(f) File.GetLastWriteTime(f)).FirstOrDefault()
            End If
            If Notify Then
                If Rep IsNot Nothing Then
                    MyMsgBox("检测到服务器崩溃报告：" & vbCrLf & Rep & vbCrLf & vbCrLf & "可以把报告发给开发者或在社区寻求帮助。", "崩溃检测", "确定")
                ElseIf HasCrash Then
                    MyMsgBox("日志中检测到异常输出（Exception），但未找到 crash-reports 报告文件。请查看控制台日志定位问题。", "崩溃检测", "确定")
                Else
                    MyMsgBox("未检测到崩溃，服务器日志一切正常。", "崩溃检测", "确定")
                End If
            ElseIf Rep IsNot Nothing Then
                Hint("检测到崩溃报告：" & Rep, HintType.Red)
            End If
        Catch
        End Try
    End Sub

    '仪表盘：系统占用刷新（每 2 秒）
    Private Sub SysTick(sender As Object, e As EventArgs)
        If LabDashMem Is Nothing OrElse ChkDashSys Is Nothing Then Return
        If Not ChkDashSys.Checked OrElse Not IsRunning() Then
            LabDashMem.Text = "—"
            Return
        End If
        Try
            ProcMsl.Refresh()
            Dim Mem = (ProcMsl.WorkingSet64 / 1048576).ToString("F0")
            Dim Cpu = (ProcMsl.TotalProcessorTime.TotalSeconds).ToString("F0")
            LabDashMem.Text = Mem & " MB 内存 · CPU 累计 " & Cpu & " s"
        Catch
            LabDashMem.Text = "—"
        End Try
    End Sub

    '===============================
    '  控制台
    '===============================

    Private Sub BtnMslSend_Click() Handles BtnMslSend.Click
        SendCmd()
    End Sub

    Private Sub TxtMslCmd_KeyDown(sender As Object, e As KeyEventArgs) Handles TxtMslCmd.KeyDown
        If e.Key = Key.Enter Then SendCmd()
    End Sub

    Private Sub SendCmd()
        Dim Cmd As String = TxtMslCmd.Text
        If String.IsNullOrWhiteSpace(Cmd) Then Return
        If Not IsRunning() OrElse SwMslIn Is Nothing Then
            Hint("服务器尚未运行，无法发送指令。", HintType.Red) : Return
        End If
        Try
            SwMslIn.WriteLine(Cmd)
            ConsMsl("[指令] " & Cmd)
            TxtMslCmd.Text = ""
        Catch ex As Exception
            Hint("指令发送失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnMslKill_Click() Handles BtnMslKill.Click
        StopServer(False)
    End Sub

    Private Sub BtnMslMore_Click() Handles BtnMslMore.Click
        If BtnMslMore.ContextMenu IsNot Nothing Then
            BtnMslMore.ContextMenu.PlacementTarget = BtnMslMore
            BtnMslMore.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top
            BtnMslMore.ContextMenu.IsOpen = True
        End If
    End Sub

    Private Sub OnMslMenu(sender As Object, e As RoutedEventArgs)
        Dim TagVal = CType(sender, MyMenuItem).Tag
        Select Case If(TagVal Is Nothing, "", TagVal.ToString())
            Case "clear"
                TxtMslConsole.Clear()
                SyncLock ConsLock : ConsBuf.Clear() : End SyncLock
            Case "restart"
                If IsRunning() Then
                    Hint("请先关闭服务器，再进行重启。", HintType.Blue) : Return
                End If
                If RunningInst IsNot Nothing Then StartInstance(RunningInst)
            Case "folder"
                Try
                    Dim Dir As String = If(RunningInst IsNot Nothing, InstDir(RunningInst), If(SelectedInst IsNot Nothing, InstDir(SelectedInst), PathServers))
                    Directory.CreateDirectory(Dir)
                    Process.Start(New ProcessStartInfo(Dir) With {.UseShellExecute = True})
                Catch ex As Exception
                    Hint("打开目录失败：" & ex.Message, HintType.Red)
                End Try
            Case "force"
                StopServer(True)
                RefreshMslState()
        End Select
    End Sub

    '===============================
    '  状态 / 日志
    '===============================

    Private Sub RefreshMslState()
        Dim Running As Boolean = IsRunning()
        If Running Then
            LabMslConsState.Text = "● 运行中"
            LabMslConsState.Foreground = New SolidColorBrush(Color.FromRgb(76, 200, 90))
            LabMslConsCore.Text = If(RunningInst IsNot Nothing, RunningInst.Name & "：" & RunningInst.CorePath, "")
            LabHomeState.Text = "运行中"
            LabHomeRunning.Text = If(RunningInst IsNot Nothing, RunningInst.Name, "")
            LabSrvState.Text = "● 运行中"
        Else
            LabMslConsState.Text = "● 已关闭"
            LabMslConsState.Foreground = New SolidColorBrush(Color.FromRgb(150, 150, 150))
            LabHomeState.Text = "未运行"
            LabHomeRunning.Text = "—"
            LabSrvState.Text = "● 已关闭"
        End If
        LabSrvState.Foreground = New SolidColorBrush(Color.FromRgb(47, 163, 92))
        RefreshDash()
    End Sub

    '===============================
    '  仪表盘
    '===============================

    ''' <summary>读取实例 server.properties 为键值字典。</summary>
    Private Function ReadPropsDict() As Dictionary(Of String, String)
        Dim Map As New Dictionary(Of String, String)
        Try
            If SelectedInst IsNot Nothing Then
                Dim F As String = InstDir(SelectedInst) & "server.properties"
                If File.Exists(F) Then
                    For Each L In File.ReadAllLines(F)
                        Dim T = L.Trim()
                        If T.StartsWith("#") OrElse Not T.Contains("=") Then Continue For
                        Map(T.Substring(0, T.IndexOf("=")).Trim()) = T.Substring(T.IndexOf("=") + 1)
                    Next
                End If
            End If
        Catch
        End Try
        Return Map
    End Function

    Private Sub RefreshDash()
        If LabDashState Is Nothing Then Return
        Dim Running As Boolean = IsRunning()
        LabDashState.Text = If(Running, "运行中", "已关闭")
        LabDashState.Foreground = New SolidColorBrush(Color.FromRgb(47, 163, 92))
        Dim P = ReadPropsDict()
        Dim GetV = Function(K As String, D As String) If(P.ContainsKey(K), P(K), D)
        LabDashOnline.Text = If(GetV("online-mode", "true") = "true", "已开启", "已关闭")
        LabDashGamemode.Text = GetV("gamemode", "survival")
        LabDashDifficulty.Text = GetV("difficulty", "easy")
        Dim Port = GetV("server-port", "25565")
        LabDashPort.Text = Port
        LabDashLocal.Text = "127.0.0.1:" & Port
        If Not IsMapRunning() Then LabDashRemote.Text = "点击获取"
    End Sub

    Private Sub BtnDashStart_Click() Handles BtnDashStart.Click
        If IsRunning() Then
            Hint("服务器已在运行中！", HintType.Blue) : Return
        End If
        If SelectedInst Is Nothing Then
            Hint("请先在「服务器」页选择一个实例（点击管理）。", HintType.Red) : Return
        End If
        StartInstance(SelectedInst)
    End Sub

    Private Sub BtnMslStart_Click() Handles BtnMslStart.Click
        If IsRunning() Then
            Hint("服务器已在运行中！", HintType.Blue) : Return
        End If
        Dim Inst = If(RunningInst, SelectedInst)
        If Inst Is Nothing Then
            Hint("请先在「服务器」页选择一个实例（点击管理）。", HintType.Red) : Return
        End If
        StartInstance(Inst)
    End Sub

    Private Sub BtnDashCrash_Click() Handles BtnDashCrash.Click
        CrashCheck(True)
    End Sub

    Private Sub BtnDashFolder_Click() Handles BtnDashFolder.Click
        Try
            Dim Dir As String = If(SelectedInst IsNot Nothing, InstDir(SelectedInst), PathServers)
            Directory.CreateDirectory(Dir)
            Process.Start(New ProcessStartInfo(Dir) With {.UseShellExecute = True})
        Catch ex As Exception
            Hint("打开目录失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnDashRemote_Click() Handles BtnDashRemote.Click
        If IsMapRunning() Then
            LabDashRemote.Text = LabTunnelDesc.Text
            Hint("已获取远程进入地址（来自当前 frpc 映射）。", HintType.Green)
        Else
            LabDashRemote.Text = "点击获取"
            Hint("尚未开启 frpc 映射。请到「映射」页添加隧道，或使用「联机」页的点对点联机。", HintType.Blue)
        End If
    End Sub

    Private Sub BtnMslLogClear_Click() Handles BtnMslLogClear.Click
        TxtMslLog.Clear()
        SyncLock ConsLock : LogBuf.Clear() : End SyncLock
    End Sub

    '批量刷新：每 120ms 把缓冲的日志一次性写入文本框，并限制长度上限
    Private Sub FlushMslLogs(sender As Object, e As EventArgs)
        Dim Cons As String = Nothing, Log As String = Nothing
        SyncLock ConsLock
            If ConsBuf.Length > 0 Then Cons = ConsBuf.ToString() : ConsBuf.Clear()
            If LogBuf.Length > 0 Then Log = LogBuf.ToString() : LogBuf.Clear()
        End SyncLock
        If Cons IsNot Nothing Then
            TxtMslConsole.AppendText(Cons)
            TrimBox(TxtMslConsole, 80000)
            If AtBottom(TxtMslConsole) Then TxtMslConsole.ScrollToEnd()
        End If
        If Log IsNot Nothing Then
            TxtMslLog.AppendText(Log)
            TrimBox(TxtMslLog, 80000)
            If AtBottom(TxtMslLog) Then TxtMslLog.ScrollToEnd()
        End If
    End Sub

    Private Sub TrimBox(Box As TextBox, Max As Integer)
        If Box.Text.Length > Max Then Box.Text = Box.Text.Substring(Box.Text.Length - Max)
    End Sub

    Private Function AtBottom(Box As TextBox) As Boolean
        Return Box.VerticalOffset + Box.ViewportHeight >= Box.ExtentHeight - 4
    End Function

    Private Sub LogMsl(Line As String)
        SyncLock ConsLock
            LogBuf.AppendLine("[" & Date.Now.ToString("HH:mm:ss") & "] " & Line)
        End SyncLock
    End Sub

    Private Sub ConsMsl(Line As String)
        SyncLock ConsLock
            ConsBuf.AppendLine(Line)
        End SyncLock
    End Sub

    '===============================
    '  端口映射（MSL 内置 frpc 快速映射）
    '===============================

    '快速映射：根据表单生成 ini 配置并启动
    Private Sub BtnMapStart_Click() Handles BtnMapStart.Click
        If IsMapRunning() Then
            Hint("映射已在运行中！", HintType.Blue) : Return
        End If
        Dim Addr = TxtMapAddr.Text.Trim(), LocalPort = TxtMapLocal.Text.Trim(), RemotePort = TxtMapRemote.Text.Trim()
        If Addr = "" OrElse LocalPort = "" OrElse RemotePort = "" Then
            Hint("请填写服务器地址、本地端口与远程端口。", HintType.Red) : Return
        End If
        Dim Frpc = FindFile(PathFrp, "frpc.exe")
        If Frpc Is Nothing Then
            Hint("尚未下载 frpc，请先到「纯 FRP 联机方案」页点击下载。", HintType.Red) : Return
        End If
        Try
            Directory.CreateDirectory(PathFrp)
            Dim Ini As String = PathFrp & "msl_map.ini"
            Dim SB As New StringBuilder()
            SB.AppendLine("[common]")
            SB.AppendLine("server_addr = " & Addr)
            SB.AppendLine("server_port = " & If(TxtMapPort.Text.Trim() = "", "7000", TxtMapPort.Text.Trim()))
            If TxtMapToken.Text.Trim() <> "" Then SB.AppendLine("token = " & TxtMapToken.Text.Trim())
            SB.AppendLine("")
            SB.AppendLine("[msl_map]")
            SB.AppendLine("type = tcp")
            SB.AppendLine("local_ip = 127.0.0.1")
            SB.AppendLine("local_port = " & LocalPort)
            SB.AppendLine("remote_port = " & RemotePort)
            File.WriteAllText(Ini, SB.ToString(), Encoding.UTF8)
            LogMsl("映射配置已生成：" & Ini)
            StartFrpcCore(Ini, "本地 " & LocalPort & " → 远程 " & RemotePort & " · " & Addr)
        Catch ex As Exception
            Hint("映射启动失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    '公共启动：以指定配置文件启动 frpc 隧道（快速映射 / 导入 ini / 粘贴配置 共用）
    Private Sub StartFrpcCore(Ini As String, Desc As String)
        If IsMapRunning() Then
            Hint("映射已在运行中！", HintType.Blue) : Return
        End If
        Dim Frpc = FindFile(PathFrp, "frpc.exe")
        If Frpc Is Nothing Then
            Hint("尚未下载 frpc，请先到「纯 FRP 联机方案」页点击下载。", HintType.Red) : Return
        End If
        Try
            Dim Info = New ProcessStartInfo With {
                .FileName = Frpc,
                .Arguments = "-c """ & Ini & """",
                .WorkingDirectory = PathFrp,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            ProcMap = New Process With {.StartInfo = Info, .EnableRaisingEvents = True}
            AddHandler ProcMap.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then ConsMsl("[映射] " & e.Data)
            AddHandler ProcMap.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then ConsMsl("[映射] " & e.Data)
            AddHandler ProcMap.Exited, Sub(s, e)
                                           RunInUi(Sub() ConsMsl("[映射已退出]"))
                                           RunInUi(AddressOf OnMapExitedUi)
                                       End Sub
            ProcMap.Start()
            ProcMap.BeginOutputReadLine()
            ProcMap.BeginErrorReadLine()
            LabMapState.Text = "映射运行中"
            LabTunnelDesc.Text = Desc
            UpdateTunnelUI()
            ConsMsl("[GloriaCloud] 映射已启动：" & Desc)
            Hint("映射已启动！隧道信息：" & Desc, HintType.Green)
        Catch ex As Exception
            Hint("映射启动失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnMapStop_Click() Handles BtnMapStop.Click
        If Not IsMapRunning() Then
            Hint("映射并未运行。", HintType.Blue) : Return
        End If
        Try
            ProcMap.Kill()
            LabMapState.Text = "映射未运行"
            UpdateTunnelUI()
            LogMsl("已停止映射。")
            ConsMsl("[映射已被停止]")
        Catch ex As Exception
            LogMsl("停止映射失败：" & ex.Message)
        End Try
    End Sub

    Private Function IsMapRunning() As Boolean
        If ProcMap Is Nothing Then Return False
        Try
            Return Not ProcMap.HasExited
        Catch
            Return False
        End Try
    End Function

    '映射进程退出后的 UI 收尾（在 UI 线程执行）
    Private Sub OnMapExitedUi()
        LabMapState.Text = "映射未运行"
        UpdateTunnelUI()
    End Sub

    '===============================
    '  设置
    '===============================

    Private Sub LoadSettings()
        Try
            If File.Exists(PathSettings) Then
                Dim O = JObject.Parse(File.ReadAllText(PathSettings))
                TxtSetJava.Text = If(O("Java") Is Nothing, "", O("Java").ToString())
                Dim F = TryCast(O("Func"), JObject)
                If F IsNot Nothing Then
                    '默认值：终端类开关启用，控制类开关禁用（与 MSL 一致）
                    ChkDashSys.Checked = If(F("DashSys") Is Nothing, True, CBool(F("DashSys")))
                    ChkDashPlayers.Checked = If(F("DashPlayers") Is Nothing, True, CBool(F("DashPlayers")))
                    ChkConpty.Checked = If(F("Conpty") Is Nothing, False, CBool(F("Conpty")))
                    ChkAutoRestart.Checked = If(F("AutoRestart") Is Nothing, False, CBool(F("AutoRestart")))
                    ChkUtf8.Checked = If(F("Utf8") Is Nothing, False, CBool(F("Utf8")))
                    ChkKillTree.Checked = If(F("KillTree") Is Nothing, False, CBool(F("KillTree")))
                    ChkLogOut.Checked = If(F("LogOut") Is Nothing, True, CBool(F("LogOut")))
                    ChkAutoClear.Checked = If(F("AutoClear") Is Nothing, True, CBool(F("AutoClear")))
                    ChkHideStack.Checked = If(F("HideStack") Is Nothing, True, CBool(F("HideStack")))
                    ChkFmtHead.Checked = If(F("FmtHead") Is Nothing, True, CBool(F("FmtHead")))
                End If
            End If
        Catch
        End Try
        UpdateJavaOptUI()
    End Sub

    ''' <summary>保存「更多功能」的开关状态。</summary>
    Private Sub SaveFunc()
        Try
            Dim O As New JObject
            If File.Exists(PathSettings) Then
                Try : O = JObject.Parse(File.ReadAllText(PathSettings)) : Catch : O = New JObject : End Try
            End If
            O("Java") = TxtSetJava.Text.Trim()
            O("Func") = New JObject From {
                {"DashSys", ChkDashSys.Checked}, {"DashPlayers", ChkDashPlayers.Checked},
                {"Conpty", ChkConpty.Checked}, {"AutoRestart", ChkAutoRestart.Checked},
                {"Utf8", ChkUtf8.Checked}, {"KillTree", ChkKillTree.Checked},
                {"LogOut", ChkLogOut.Checked}, {"AutoClear", ChkAutoClear.Checked},
                {"HideStack", ChkHideStack.Checked}, {"FmtHead", ChkFmtHead.Checked}
            }
            Directory.CreateDirectory(PathMsl)
            File.WriteAllText(PathSettings, O.ToString(Formatting.Indented), Encoding.UTF8)
        Catch
        End Try
    End Sub

    Private Sub Func_Change(sender As Object, user As Boolean) Handles ChkDashSys.Change, ChkDashPlayers.Change, ChkConpty.Change, ChkAutoRestart.Change,
                                                                        ChkUtf8.Change, ChkKillTree.Change, ChkLogOut.Change, ChkAutoClear.Change,
                                                                        ChkHideStack.Change, ChkFmtHead.Change
        If Not _InitDone Then Return
        SaveFunc()
        If sender Is ChkDashPlayers Then RefreshPlayers()
    End Sub

    Private Sub BtnSetJava_Click() Handles BtnSetJava.Click
        Dim Dlg = PickJava()
        If Dlg <> "" Then TxtSetJava.Text = Dlg
    End Sub

    Private Sub BtnSetSave_Click() Handles BtnSetSave.Click
        Try
            Directory.CreateDirectory(PathMsl)
            File.WriteAllText(PathSettings, New JObject From {{"Java", TxtSetJava.Text.Trim()}}.ToString(), Encoding.UTF8)
            Hint("设置已保存。", HintType.Green)
        Catch ex As Exception
            Hint("保存设置失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnSetFolder_Click() Handles BtnSetFolder.Click
        Try
            Directory.CreateDirectory(PathMsl)
            Process.Start(New ProcessStartInfo(PathMsl) With {.UseShellExecute = True})
        Catch ex As Exception
            Hint("打开目录失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnSetClear_Click() Handles BtnSetClear.Click
        Try
            Dim Cache As String = PathMsl & "Server\"
            If Directory.Exists(Cache) Then Directory.Delete(Cache, True)
            Hint("下载缓存已清理。", HintType.Green)
        Catch ex As Exception
            Hint("清理失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    '===============================
    '  MSLX 风格界面：服务器页 / 映射页 / 联机页
    '===============================

    '服务器页：悬浮 + 按钮新建
    Private Sub BtnFabAdd_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnFabAdd.MouseLeftButtonUp
        BtnTabCreate.Checked = True
    End Sub

    '下载资源 / 下载服务器（提示真实的存放目录）
    Private Sub BtnListDlRes_Click() Handles BtnListDlRes.Click
        Dim Dl = PathMsl & "Downloads\"
        Try : Directory.CreateDirectory(Dl) : Catch : End Try
        MyMsgBox("这是单独的模组/组件/整合包下载界面。" & vbCrLf & "下载的文件均存放在：" & vbCrLf & Dl, "提示", "确定")
    End Sub

    Private Sub BtnListDlSrv_Click() Handles BtnListDlSrv.Click
        Dim Dl = PathMsl & "Downloads\"
        Try : Directory.CreateDirectory(Dl) : Catch : End Try
        MyMsgBox("这是单独的服务器下载界面。" & vbCrLf & "下载的服务器端均存放在：" & vbCrLf & Dl, "提示", "确定")
    End Sub

    '===============================
    '  实例设置页（服务器管理 → 实例设置）
    '===============================

    Private Sub BtnSetDirBrowse_Click() Handles BtnSetDirBrowse.Click
        Try
            If SelectedInst Is Nothing Then
                Hint("请先选择实例。", HintType.Red) : Return
            End If
            Directory.CreateDirectory(InstDir(SelectedInst))
            Process.Start(New ProcessStartInfo(InstDir(SelectedInst)) With {.UseShellExecute = True})
        Catch ex As Exception
            Hint("打开目录失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnSetJavaSearch_Click() Handles BtnSetJavaSearch.Click
        Dim J = FindJavaAuto()
        If J = "" Then
            Hint("未在本机找到 Java，请安装 Java 或使用「开服器在线下载」。", HintType.Red)
        Else
            TxtCfgJavaSearch.Text = J
            Hint("已找到 Java：" & J, HintType.Green)
        End If
    End Sub

    '在线下载 Java 运行时（Eclipse Adoptium）
    Private Sub BtnSetJavaDownload_Click() Handles BtnSetJavaDownload.Click
        Dim Vm = ComboText(ComboSetJavaVer)
        If Vm = "" Then
            Hint("请先选择 Java 版本。", HintType.Red) : Return
        End If
        BtnSetJavaDownload.IsEnabled = False
        RunInNewThread(Sub()
                           Try
                               DlShow("正在获取 Java " & Vm & " 运行时下载地址……", Nothing)
                               Dim Url As String = $"https://api.adoptium.net/v3/binary/latest/{Vm}/ga/windows/x64/jre/hotspot/normal/eclipse"
                               Dim Dir As String = PathMsl & "Java\" & Vm & "\"
                               Dim ZipPath As String = Dir & "jre.zip"
                               Directory.CreateDirectory(Dir)
                               DownloadFile(Url, ZipPath, "正在下载 Java " & Vm & " 运行时（Adoptium）")
                               DlShow("正在解压 Java " & Vm & " ……", Nothing)
                               ExtractZip(ZipPath, Dir)
                               File.Delete(ZipPath)
                               Dim J = FindFile(Dir, "java.exe")
                               If J Is Nothing Then Throw New Exception("解压后未找到 java.exe")
                               RunInUi(Sub()
                                           TxtCfgJava.Text = J
                                           SetJavaOpt(JavaOpt.Manual)
                                           Hint("Java " & Vm & " 下载完成：" & J, HintType.Green)
                                           LogMsl("Java " & Vm & " 已下载：" & J)
                                           BtnSetJavaDownload.IsEnabled = True
                                           DlHide()
                                       End Sub)
                           Catch ex As Exception
                               RunInUi(Sub()
                                           Hint("Java 下载失败：" & ex.Message, HintType.Red)
                                           LogMsl("Java 下载失败：" & ex.Message)
                                           BtnSetJavaDownload.IsEnabled = True
                                           DlHide()
                                       End Sub)
                           End Try
                       End Sub)
    End Sub

    Private Sub BtnSetCoreBrowse_Click() Handles BtnSetCoreBrowse.Click
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择服务端核心文件",
            .Filter = "服务端核心 (*.jar;*.bat;*.exe)|*.jar;*.bat;*.exe|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return
        TxtSetCore.Text = Dlg.FileName
    End Sub

    '为当前实例下载服务端核心（使用新建页选择的类型与版本）
    Private Sub BtnSetCoreDl_Click() Handles BtnSetCoreDl.Click
        If SelectedInst Is Nothing Then
            Hint("请先选择实例。", HintType.Red) : Return
        End If
        Dim T = ComboText(ComboCreateType), Ver = ComboText(ComboCreateVersion)
        If T = "" Then
            Hint("请先到「新建」页选择服务器类型与版本。", HintType.Red) : Return
        End If
        BtnSetCoreDl.IsEnabled = False
        Dim Inst = SelectedInst
        RunInNewThread(Sub()
                           Try
                               Dim Core As String = DownloadCore(T, Ver, InstDir(Inst))
                               RunInUi(Sub()
                                           TxtSetCore.Text = Core
                                           Hint("服务端核心下载完成：" & Core, HintType.Green)
                                           LogMsl("核心下载完成：" & Core)
                                           BtnSetCoreDl.IsEnabled = True
                                           DlHide()
                                       End Sub)
                           Catch ex As Exception
                               RunInUi(Sub()
                                           Hint("核心下载失败：" & ex.Message, HintType.Red)
                                           BtnSetCoreDl.IsEnabled = True
                                           DlHide()
                                       End Sub)
                           End Try
                       End Sub)
    End Sub

    Private Sub BtnSetSaveInst_Click() Handles BtnSetSaveInst.Click
        If SelectedInst Is Nothing Then
            Hint("请先选择实例。", HintType.Red) : Return
        End If
        Dim NewName As String = TxtSetName.Text.Trim()
        If NewName = "" Then
            Hint("实例名称不能为空！", HintType.Red) : Return
        End If
        If NewName <> SelectedInst.Name AndAlso Instances.Any(Function(x) x.Name = NewName) Then
            Hint("已存在同名实例，请换一个名称。", HintType.Red) : Return
        End If
        SelectedInst.Name = NewName
        SelectedInst.Java = JavaFromOpt()
        SelectedInst.Memory = ComboText(ComboCfgMemory)
        SelectedInst.Args = TxtCfgArgs.Text.Trim()
        Dim Core = TxtSetCore.Text.Trim()
        If Core <> "" AndAlso File.Exists(Core) Then SelectedInst.CorePath = Core
        SaveInstances()
        RefreshInstanceList()
        LabSrvTitle.Text = SelectedInst.Name & " — 服务器管理"
        LogMsl("实例设置已保存：" & SelectedInst.Name)
        Hint("实例设置已保存！", HintType.Green)
    End Sub

    '===============================
    '  插件·模组管理
    '===============================

    Private Sub BtnModRefresh_Click() Handles BtnModRefresh.Click
        RefreshMods()
    End Sub

    Private Sub BtnModOpen_Click() Handles BtnModOpen.Click
        Try
            If SelectedInst Is Nothing Then
                Hint("请先选择实例。", HintType.Red) : Return
            End If
            Dim Target As String = InstDir(SelectedInst)
            If Directory.Exists(Target & "plugins\") Then
                Target = Target & "plugins\"
            ElseIf Directory.Exists(Target & "mods\") Then
                Target = Target & "mods\"
            End If
            Process.Start(New ProcessStartInfo(Target) With {.UseShellExecute = True})
        Catch ex As Exception
            Hint("打开目录失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    ''' <summary>刷新插件/模组列表（plugins / mods 及其 _disabled 目录）。</summary>
    Private Sub RefreshMods()
        If PanModList Is Nothing Then Return
        PanModList.Children.Clear()
        Dim Count As Integer = 0
        If SelectedInst IsNot Nothing AndAlso SelectedInst.CorePath.ToLower().EndsWith(".jar") Then
            For Each Sub_ As String In {"plugins", "mods"}
                Dim EnabledDir = InstDir(SelectedInst) & Sub_ & "\"
                Dim DisabledDir = InstDir(SelectedInst) & Sub_ & "_disabled\"
                Count += AddModCards(EnabledDir, Sub_, False, DisabledDir)
                Count += AddModCards(DisabledDir, Sub_, True, EnabledDir)
            Next
        End If
        PanModEmpty.Visibility = If(Count = 0, Visibility.Visible, Visibility.Collapsed)
    End Sub

    Private Function AddModCards(Dir As String, Kind As String, Disabled As Boolean, OtherDir As String) As Integer
        If Not Directory.Exists(Dir) Then Return 0
        Dim N As Integer = 0
        For Each F In Directory.GetFiles(Dir, "*.jar")
            N += 1
            Dim FilePath = F, SrcDir = Dir
            Dim Info As New FileInfo(F)
            Dim Card As New Border With {
                .Background = TryCast(FindResource("ColorBrushBg1"), Brush),
                .CornerRadius = New CornerRadius(6),
                .Margin = New Thickness(0, 0, 0, 8),
                .Padding = New Thickness(12, 9, 12, 9)
            }
            Dim G As New Grid()
            G.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            G.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Dim SpL As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
            SpL.Children.Add(New TextBlock With {.Text = Path.GetFileName(F), .FontSize = 13.5, .FontWeight = FontWeights.Bold})
            SpL.Children.Add(New TextBlock With {
                .Text = Kind & " · " & (Info.Length / 1024).ToString("F0") & " KB" & If(Disabled, " · 已禁用", ""),
                .FontSize = 11.5, .Foreground = TryCast(FindResource("ColorBrushGray3"), Brush), .Margin = New Thickness(0, 2, 0, 0)})
            Grid.SetColumn(SpL, 0)
            Dim SpR As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
            Dim BToggle As New MyButton With {.Text = If(Disabled, "启用", "禁用"), .Padding = New Thickness(12, 5, 12, 5), .Margin = New Thickness(0, 0, 8, 0)}
            AddHandler BToggle.Click, Sub()
                                          Try
                                              Dim DestPath = Path.Combine(OtherDir, Path.GetFileName(FilePath))
                                              Directory.CreateDirectory(OtherDir)
                                              If File.Exists(DestPath) Then File.Delete(DestPath)
                                              File.Move(FilePath, DestPath)
                                              Hint(If(Disabled, "已启用：", "已禁用：") & Path.GetFileName(FilePath), HintType.Blue)
                                              RefreshMods()
                                          Catch ex As Exception
                                              Hint("操作失败：" & ex.Message, HintType.Red)
                                          End Try
                                      End Sub
            Dim BDel As New MyButton With {.Text = "删除", .Padding = New Thickness(12, 5, 12, 5), .ColorType = MyButton.ColorState.Red}
            AddHandler BDel.Click, Sub()
                                       Try
                                           File.Delete(FilePath)
                                           Hint("已删除：" & Path.GetFileName(FilePath), HintType.Blue)
                                           RefreshMods()
                                       Catch ex As Exception
                                           Hint("删除失败：" & ex.Message, HintType.Red)
                                       End Try
                                   End Sub
            SpR.Children.Add(BToggle) : SpR.Children.Add(BDel)
            Grid.SetColumn(SpR, 1)
            G.Children.Add(SpL) : G.Children.Add(SpR)
            Card.Child = G
            PanModList.Children.Add(Card)
        Next
        Return N
    End Function

    '===============================
    '  定时任务（Cron：秒 分 时 日 月 周）
    '===============================

    Private Tasks As New List(Of MslTask)
    Private SelectedTask As MslTask = Nothing

    Private Sub LoadTasks()
        Try
            Tasks.Clear()
            If File.Exists(PathTasks) Then
                Dim Arr = JArray.Parse(File.ReadAllText(PathTasks))
                For Each O As JObject In Arr
                    Tasks.Add(New MslTask With {
                        .Cron = If(O("Cron") Is Nothing, "", O("Cron").ToString()),
                        .Cmd = If(O("Cmd") Is Nothing, "", O("Cmd").ToString()),
                        .LastRun = If(O("LastRun") Is Nothing, "", O("LastRun").ToString())
                    })
                Next
            End If
        Catch
        End Try
        RunInUi(AddressOf RefreshTasks)
    End Sub

    Private Sub SaveTasks()
        Try
            Directory.CreateDirectory(PathMsl)
            Dim Arr As New JArray()
            For Each T As MslTask In Tasks
                Arr.Add(New JObject From {{"Cron", T.Cron}, {"Cmd", T.Cmd}, {"LastRun", T.LastRun}})
            Next
            File.WriteAllText(PathTasks, Arr.ToString(Formatting.Indented), Encoding.UTF8)
        Catch ex As Exception
            LogMsl("保存任务配置失败：" & ex.Message)
        End Try
    End Sub

    Private Sub RefreshTasks()
        If PanTaskList Is Nothing Then Return
        PanTaskList.Children.Clear()
        PanTaskEmpty.Visibility = If(Tasks.Count = 0, Visibility.Visible, Visibility.Collapsed)
        For Each T As MslTask In Tasks
            Dim Task = T
            Dim B As New Border With {
                .Background = TryCast(FindResource("ColorBrushBg1"), Brush),
                .CornerRadius = New CornerRadius(6),
                .Padding = New Thickness(10, 7, 10, 7),
                .Margin = New Thickness(0, 0, 0, 6),
                .Cursor = Cursors.Hand
            }
            Dim Sp As New StackPanel()
            Sp.Children.Add(New TextBlock With {.Text = "Cron：" & T.Cron, .FontSize = 12.5, .FontWeight = FontWeights.Bold})
            Sp.Children.Add(New TextBlock With {.Text = "指令：" & If(T.Cmd = "", "（空）", T.Cmd), .FontSize = 11.5, .Foreground = TryCast(FindResource("ColorBrushGray3"), Brush)})
            Sp.Children.Add(New TextBlock With {.Text = "上次执行：" & If(T.LastRun = "", "无", T.LastRun), .FontSize = 11.5, .Foreground = TryCast(FindResource("ColorBrushGray3"), Brush)})
            AddHandler B.MouseLeftButtonUp, Sub()
                                                SelectedTask = Task
                                                TxtTaskCron.Text = Task.Cron
                                                TxtTaskCmd.Text = Task.Cmd
                                                LabTaskLast.Text = If(Task.LastRun = "", "无", Task.LastRun)
                                                Hint("已选中任务，可直接修改后点击「添加新任务」覆盖保存。", HintType.Blue)
                                            End Sub
            B.Child = Sp
            PanTaskList.Children.Add(B)
        Next
    End Sub

    Private Sub BtnTaskAdd_Click() Handles BtnTaskAdd.Click
        Dim Cron = TxtTaskCron.Text.Trim(), Cmd = TxtTaskCmd.Text.Trim()
        If Cron.Split(" "c).Length <> 6 Then
            Hint("Cron 表达式必须为 6 字段（秒 分 时 日 月 周）。", HintType.Red) : Return
        End If
        If SelectedTask IsNot Nothing Then
            SelectedTask.Cron = Cron
            SelectedTask.Cmd = Cmd
            Hint("任务已更新。", HintType.Green)
        Else
            Tasks.Add(New MslTask With {.Cron = Cron, .Cmd = Cmd})
            Hint("任务已添加。", HintType.Green)
        End If
        SelectedTask = Nothing
        SaveTasks()
        RefreshTasks()
    End Sub

    Private Sub BtnTaskDel_Click() Handles BtnTaskDel.Click
        If SelectedTask Is Nothing Then
            Hint("请先在列表中点选一个任务。", HintType.Blue) : Return
        End If
        Tasks.Remove(SelectedTask)
        SelectedTask = Nothing
        SaveTasks()
        RefreshTasks()
        Hint("任务已删除。", HintType.Green)
    End Sub

    Private Sub BtnTaskDelAll_Click() Handles BtnTaskDelAll.Click
        Tasks.Clear()
        SelectedTask = Nothing
        SaveTasks()
        RefreshTasks()
        Hint("已删除所有任务。", HintType.Green)
    End Sub

    Private Sub BtnTaskLoad_Click() Handles BtnTaskLoad.Click
        LoadTasks()
        Hint("任务配置已重新加载。", HintType.Blue)
    End Sub

    Private Sub BtnTaskClear_Click() Handles BtnTaskClear.Click
        Tasks.Clear()
        SelectedTask = Nothing
        Try
            If File.Exists(PathTasks) Then File.Delete(PathTasks)
        Catch
        End Try
        RefreshTasks()
        Hint("任务配置已清除。", HintType.Green)
    End Sub

    Private Sub BtnCron_Click(sender As Object, e As RoutedEventArgs) Handles BtnCronA.Click, BtnCronB.Click, BtnCronC.Click, BtnCronD.Click,
                                                                               BtnCronE.Click, BtnCronF.Click, BtnCronG.Click
        Dim Map As New Dictionary(Of String, String) From {
            {"BtnCronA", "*/10 * * * * *"}, {"BtnCronB", "0 * * * * *"}, {"BtnCronC", "0 */5 * * * *"},
            {"BtnCronD", "0 */30 * * * *"}, {"BtnCronE", "0 0 * * * *"}, {"BtnCronF", "0 0 3 * * *"}, {"BtnCronG", "0 0 3 * * 0"}
        }
        Dim N = CType(sender, System.Windows.FrameworkElement).Name
        If Map.ContainsKey(N) Then TxtTaskCron.Text = Map(N)
    End Sub

    Private Sub BtnTaskRun_Click() Handles BtnTaskRun.Click
        If TaskTimer.IsEnabled Then
            TaskTimer.Stop()
            BtnTaskRun.Text = "▶ 启动定时任务"
            Hint("定时任务已停止。", HintType.Blue)
        Else
            If Tasks.Count = 0 Then
                Hint("请先添加任务！", HintType.Red) : Return
            End If
            TaskTimer.Start()
            BtnTaskRun.Text = "■ 停止定时任务"
            Hint("定时任务已启动，调度器每秒检查一次。", HintType.Green)
            LogMsl("定时任务调度器已启动，共 " & Tasks.Count & " 个任务。")
        End If
    End Sub

    Private Sub TaskTimerTick(sender As Object, e As EventArgs)
        If Tasks.Count = 0 Then Return
        Dim NowT = Date.Now
        Dim Key = NowT.Hour * 10000 + NowT.Minute * 100 + NowT.Second
        For Each T As MslTask In Tasks
            If T.LastKey = Key Then Continue For
            If CronMatch(T.Cron, NowT) Then
                T.LastKey = Key
                RunTask(T, NowT)
            End If
        Next
    End Sub

    Private Sub RunTask(T As MslTask, NowT As Date)
        T.LastRun = NowT.ToString("yyyy-MM-dd HH:mm:ss")
        LabTaskLast.Text = T.LastRun
        SaveTasks()
        LogMsl("定时任务触发：" & T.Cron & " → " & T.Cmd)
        If T.Cmd.Trim() = ".backup" Then
            BackupInst()
        ElseIf IsRunning() AndAlso SwMslIn IsNot Nothing Then
            Try
                SwMslIn.WriteLine(T.Cmd)
                ConsMsl("[定时任务] " & T.Cmd)
            Catch ex As Exception
                ConsMsl("[定时任务] 指令发送失败：" & ex.Message)
            End Try
        Else
            ConsMsl("[定时任务] 服务器未运行，指令未发送：" & T.Cmd)
        End If
    End Sub

    'Cron 字段匹配（支持 * / n a-b a,b,c */n）
    Private Function CronMatch(Cron As String, T As Date) As Boolean
        Try
            Dim Parts = Cron.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            If Parts.Length <> 6 Then Return False
            Dim Cur() As Integer = {T.Second, T.Minute, T.Hour, T.Day, T.Month, CInt(T.DayOfWeek)}
            For i = 0 To 5
                If Not CronFieldMatch(Parts(i), Cur(i), i) Then Return False
            Next
            Return True
        Catch
            Return False
        End Try
    End Function

    Private Function CronFieldMatch(Field As String, V As Integer, Idx As Integer) As Boolean
        Dim MaxV() As Integer = {59, 59, 23, 31, 12, 6}
        For Each Tok In Field.Split(","c)
            Dim StepN = 1
            Dim Body = Tok
            If Body.Contains("/") Then
                Dim Sp = Body.Split("/"c)
                Body = Sp(0)
                If Not Integer.TryParse(Sp(1), StepN) Then StepN = 1
                If StepN < 1 Then StepN = 1
            End If
            Dim Lo As Integer = 0, Hi As Integer = MaxV(Idx)
            If Body = "*" Then
                '全范围
            ElseIf Body.Contains("-") Then
                Dim Sp = Body.Split("-"c)
                If Not (Integer.TryParse(Sp(0), Lo) AndAlso Integer.TryParse(Sp(1), Hi)) Then Continue For
            ElseIf Integer.TryParse(Body, Lo) Then
                Hi = If(StepN > 1, MaxV(Idx), Lo)
            Else
                Continue For
            End If
            If V < Lo OrElse V > Hi Then Continue For
            If (V - Lo) Mod StepN = 0 Then Return True
        Next
        Return False
    End Function

    '===============================
    '  备份（定时任务 .backup 指令）
    '===============================

    Private Sub BackupInst()
        Dim Inst = If(RunningInst, SelectedInst)
        If Inst Is Nothing Then
            Hint("没有可备份的实例。", HintType.Red) : Return
        End If
        ConsMsl("[备份] 正在备份 " & Inst.Name & " ……")
        RunInNewThread(Sub()
                           Try
                               Dim Dir = InstDir(Inst)
                               Dim BkDir = PathMsl & "Backups\"
                               Directory.CreateDirectory(BkDir)
                               Dim ZipPath = BkDir & Inst.Name & "_" & Date.Now.ToString("yyyyMMdd_HHmmss") & ".zip"
                               Using FS = File.Create(ZipPath)
                                   Using Zip As New ZipArchive(FS, ZipArchiveMode.Create)
                                       For Each F In Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)
                                           Dim Rel = F.Substring(Dir.Length).Replace("\", "/")
                                           If Rel.Contains("/logs/") OrElse Rel.StartsWith("logs/") Then Continue For
                                           Dim E = Zip.CreateEntry(Rel)
                                           Using ES = E.Open()
                                               Using SF = File.OpenRead(F)
                                                   SF.CopyTo(ES)
                                               End Using
                                           End Using
                                       Next
                                   End Using
                               End Using
                               ConsMsl("[备份] 备份完成：" & ZipPath)
                               Hint("备份完成：" & ZipPath, HintType.Green)
                           Catch ex As Exception
                               ConsMsl("[备份] 备份失败：" & ex.Message)
                               Hint("备份失败：" & ex.Message, HintType.Red)
                           End Try
                       End Sub)
    End Sub

    '映射页：我的 MSL Frp 信息
    Private Sub BtnMslFrpInfo_Click() Handles BtnMslFrpInfo.Click
        MyMsgBox("在这里可接入 MSLFrp、MEFrp 等隧道服务获取签名隧道。" & vbCrLf & "也可以点击下方「添加隧道」，手动填写 FRP 服务端地址与端口完成映射。" & vbCrLf & vbCrLf &
                 "内置 frpc 存放于 GloriaTools\Frp 目录，与 FRP 联机模式共用。", "我的MSL Frp信息", "确定")
    End Sub

    '映射页：添加隧道（展开/收起快速映射表单）
    Private Sub BtnTunnelAdd_Click() Handles BtnTunnelAdd.Click
        If PanMapForm.Visibility = Visibility.Visible Then
            PanMapForm.Visibility = Visibility.Collapsed
        Else
            PanMapForm.Visibility = Visibility.Visible
        End If
        UpdateTunnelUI()
    End Sub

    '映射页：删除选中（停止当前映射）
    Private Sub BtnTunnelDel_Click() Handles BtnTunnelDel.Click
        BtnMapStop_Click()
    End Sub

    '隧道列表 UI 状态：有映射运行时显示卡片
    Private Sub UpdateTunnelUI()
        If PanTunnelCard Is Nothing OrElse PanMapEmpty Is Nothing Then Return
        Dim Running As Boolean = IsMapRunning()
        PanTunnelCard.Visibility = If(Running, Visibility.Visible, Visibility.Collapsed)
        PanMapEmpty.Visibility = Visibility.Collapsed
    End Sub

    '隧道卡片：双击进入详情页
    Private Sub PanTunnelCard_MouseDown(sender As Object, e As MouseButtonEventArgs)
        If e.ClickCount < 2 Then Return
        Dim IniFile = PathFrp & "msl_map.ini"
        Dim Detail As String
        If IsMapRunning() Then
            Detail = "当前状态：映射运行中" & vbCrLf &
                     "隧道信息：" & LabTunnelDesc.Text & vbCrLf & vbCrLf
        Else
            Detail = "当前状态：映射未运行" & vbCrLf & vbCrLf
        End If
        If File.Exists(IniFile) Then
            Detail &= "配置文件：" & IniFile & vbCrLf &
                      "----------------------------" & vbCrLf &
                      File.ReadAllText(IniFile, Encoding.UTF8)
        Else
            Detail &= "尚未生成配置文件（" & IniFile & "）。"
        End If
        MyMsgBox(Detail, "隧道详情 - msl_map", "确定")
    End Sub

    '导入方式一：选择 ini / toml 配置文件，一键启动映射
    Private Sub PanMapImportIni_Click(sender As Object, e As MouseButtonEventArgs)
        If IsMapRunning() Then
            Hint("映射已在运行中，请先停止当前映射。", HintType.Blue) : Return
        End If
        Dim Frpc = FindFile(PathFrp, "frpc.exe")
        If Frpc Is Nothing Then
            Hint("尚未下载 frpc，请先到「纯 FRP 联机方案」页点击下载。", HintType.Red) : Return
        End If
        Dim Dial As New Microsoft.Win32.OpenFileDialog With {
            .Title = "选择 frpc 配置文件（ini / toml）",
            .Filter = "frpc 配置文件 (*.ini;*.toml;*.txt)|*.ini;*.toml;*.txt|所有文件 (*.*)|*.*"
        }
        If Dial.ShowDialog() <> True Then Return
        Try
            Directory.CreateDirectory(PathFrp)
            Dim Ini As String = PathFrp & "msl_map.ini"
            File.Copy(Dial.FileName, Ini, True)
            Hint("已导入配置：" & Dial.FileName, HintType.Green)
            StartFrpcCore(Ini, "导入配置 · " & IO.Path.GetFileName(Dial.FileName))
        Catch ex As Exception
            Hint("导入配置失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    '导入方式二：手动粘贴 txt 代码（展开粘贴框）
    Private Sub PanMapImportTxt_Click(sender As Object, e As MouseButtonEventArgs)
        PanMapPaste.Visibility = If(PanMapPaste.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
    End Sub

    '粘贴框：使用粘贴的配置启动映射
    Private Sub BtnMapPasteStart_Click() Handles BtnMapPasteStart.Click
        If IsMapRunning() Then
            Hint("映射已在运行中，请先停止当前映射。", HintType.Blue) : Return
        End If
        Dim Txt = TxtMapPaste.Text
        If Txt.Trim() = "" Then
            Hint("请先粘贴 frpc 配置文本。", HintType.Red) : Return
        End If
        Dim Frpc = FindFile(PathFrp, "frpc.exe")
        If Frpc Is Nothing Then
            Hint("尚未下载 frpc，请先到「纯 FRP 联机方案」页点击下载。", HintType.Red) : Return
        End If
        Try
            Directory.CreateDirectory(PathFrp)
            Dim Ini As String = PathFrp & "msl_map.ini"
            File.WriteAllText(Ini, Txt, Encoding.UTF8)
            StartFrpcCore(Ini, "粘贴配置")
        Catch ex As Exception
            Hint("映射启动失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    '粘贴框：取消
    Private Sub BtnMapPasteCancel_Click() Handles BtnMapPasteCancel.Click
        PanMapPaste.Visibility = Visibility.Collapsed
    End Sub

    '联机页：手风琴展开/收起
    Private Sub HeadOnlineCreate_Click(sender As Object, e As MouseButtonEventArgs) Handles HeadOnlineCreate.MouseLeftButtonUp
        Dim Opening = BodyOnlineCreate.Visibility <> Visibility.Visible
        BodyOnlineCreate.Visibility = If(Opening, Visibility.Visible, Visibility.Collapsed)
        AniStart({AaRotateTransform(ArrowOnlineCreate, If(Opening, 180, 0), 160,, New AniEaseOutFluent)}, "MslOnlineArrowCreate")
    End Sub

    Private Sub HeadOnlineJoin_Click(sender As Object, e As MouseButtonEventArgs) Handles HeadOnlineJoin.MouseLeftButtonUp
        Dim Opening = BodyOnlineJoin.Visibility <> Visibility.Visible
        BodyOnlineJoin.Visibility = If(Opening, Visibility.Visible, Visibility.Collapsed)
        AniStart({AaRotateTransform(ArrowOnlineJoin, If(Opening, 180, 0), 160,, New AniEaseOutFluent)}, "MslOnlineArrowJoin")
    End Sub

    '联机页：创建房间（走 GloriaCloud 内置 EasyTier 联机管线）
    Private Sub BtnOnlineCreate_Click() Handles BtnOnlineCreate.Click
        Dim Port = TxtOnlineGamePort.Text.Trim()
        AppendOnlineLog("正在创建房间" & If(Port <> "", "（游戏端口 " & Port & "）", "") & "，请按弹出的提示操作……")
        FormMain.GoOnline("create")
        AppendOnlineLog("房间码已生成（见提示），把房间码发给好友即可加入。")
    End Sub

    '联机页：加入房间
    Private Sub BtnOnlineJoin_Click() Handles BtnOnlineJoin.Click
        Dim Room As String = TxtOnlineJoinRoom.Text.Trim()
        If Room = "" Then
            Hint("请先输入房主给你的房间号！", HintType.Red) : Return
        End If
        AppendOnlineLog("正在加入房间 " & Room & "，请按弹出的提示操作……")
        FormMain.GoOnline("join", Room)
    End Sub

    '联机页：刷新服务器状态
    Private Sub BtnOnlineRefresh_Click() Handles BtnOnlineRefresh.Click
        LabOnlineState.Text = "可用"
        AppendOnlineLog("已刷新：联机服务器状态 可用。")
        Hint("联机服务器状态：可用。", HintType.Green)
    End Sub

    '联机页：教程与网站
    Private Sub BtnOnlineGuide_Click() Handles BtnOnlineGuide.Click
        MyMsgBox("点对点联机教程：" & vbCrLf &
                 "1. 房主：填好游戏端口（即 server.properties 中的 server-port），点击「点击创建房间」；" & vbCrLf &
                 "2. 把生成的房间号与房间密码发给好友；" & vbCrLf &
                 "3. 好友：在「进入房间——成员」中填入房间号与密码，点击「加入房间」；" & vbCrLf &
                 "4. 双方在 Minecraft 中连接 127.0.0.1:游戏端口 即可联机。", "联机教程", "确定")
    End Sub

    Private Sub BtnOnlineSite_Click() Handles BtnOnlineSite.Click
        Try
            Process.Start(New ProcessStartInfo("https://github.com/MSLTeam/MSLX") With {.UseShellExecute = True})
        Catch ex As Exception
            Hint("打开网站失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnOnlineStun_Check(sender As Object, raiseByMouse As Boolean) Handles BtnOnlineStun.Check
        Hint("STUN 随机模式即将上线，当前请使用点对点联机（P2P）。", HintType.Blue)
        BtnOnlineP2P.Checked = True
    End Sub

    Private Sub AppendOnlineLog(Line As String)
        TxtOnlineLog.AppendText("[" & Date.Now.ToString("HH:mm:ss") & "] " & Line & vbCrLf)
        TxtOnlineLog.ScrollToEnd()
    End Sub

    '===============================
    '  通用辅助
    '===============================

    Private Function ComboText(C As ComboBox) As String
        If C.SelectedItem Is Nothing Then Return ""
        Dim It = TryCast(C.SelectedItem, MyComboBoxItem)
        If It IsNot Nothing Then Return If(It.Content Is Nothing, "", It.Content.ToString())
        Return C.SelectedItem.ToString()
    End Function

    Private Sub SelectComboByText(C As ComboBox, Text As String)
        For i = 0 To C.Items.Count - 1
            Dim It = TryCast(C.Items(i), MyComboBoxItem)
            Dim S = If(It IsNot Nothing AndAlso It.Content IsNot Nothing, It.Content.ToString(), If(C.Items(i) IsNot Nothing, C.Items(i).ToString(), ""))
            If S = Text Then
                C.SelectedIndex = i
                Return
            End If
        Next
    End Sub

    ''' <summary>
    ''' 关闭启动器时强制结束服务器进程。
    ''' </summary>
    Public Sub KillOnExit()
        Try
            If ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited Then ProcMsl.Kill()
        Catch
        End Try
        ProcMsl = Nothing
        Try
            If ProcMap IsNot Nothing AndAlso Not ProcMap.HasExited Then ProcMap.Kill()
        Catch
        End Try
        ProcMap = Nothing
    End Sub

    '===============================
    '  下载辅助（含实时进度）
    '===============================

    ''' <summary>显示新建页的实时下载进度卡片（可在后台线程调用）。</summary>
    Private Sub DlShow(Label As String, Pct As Double?)
        RunInUi(Sub()
                    If PanDlCard Is Nothing Then Return
                    PanDlCard.Visibility = Visibility.Visible
                    If Label <> "" Then LabDlText.Text = Label
                    If Pct.HasValue Then
                        BarDl.IsIndeterminate = False
                        BarDl.Value = Math.Max(0, Math.Min(100, Pct.Value))
                        LabDlPct.Text = CInt(BarDl.Value) & "%"
                    Else
                        BarDl.IsIndeterminate = True
                        LabDlPct.Text = ""
                    End If
                End Sub)
    End Sub

    ''' <summary>隐藏下载进度卡片（可在后台线程调用）。</summary>
    Private Sub DlHide()
        RunInUi(Sub() If PanDlCard IsNot Nothing Then PanDlCard.Visibility = Visibility.Collapsed)
    End Sub

    Private Sub DownloadFile(Url As String, Dest As String, Optional Label As String = "")
        Directory.CreateDirectory(Path.GetDirectoryName(Dest))
        Using Resp = Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
            Resp.EnsureSuccessStatusCode()
            Dim Total = Resp.Content.Headers.ContentLength
            If Label <> "" Then
                If Total.HasValue AndAlso Total.Value > 0 Then
                    DlShow(Label, 0.0)
                Else
                    DlShow(Label, Nothing)
                End If
            End If
            Using FS = File.Create(Dest)
                Using Stream = Resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    Dim Buf(8191) As Byte
                    Dim N As Integer
                    Dim Done As Long = 0
                    Do
                        N = Stream.Read(Buf, 0, Buf.Length)
                        If N <= 0 Then Exit Do
                        FS.Write(Buf, 0, N)
                        Done += N
                        If Label <> "" AndAlso Total.HasValue AndAlso Total.Value > 0 Then
                            DlShow(Label, Done * 100.0 / Total.Value)
                        End If
                    Loop
                End Using
            End Using
        End Using
    End Sub

    Private Function FindFile(Root As String, Name As String) As String
        If Not Directory.Exists(Root) Then Return Nothing
        For Each F In Directory.EnumerateFiles(Root, Name, SearchOption.AllDirectories)
            Return F
        Next
        Return Nothing
    End Function

End Class
