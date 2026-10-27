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
    End Class

    '===============================
    '  字段与初始化
    '===============================

    Private Shared ReadOnly Client As New HttpClient()
    Private PathMsl As String = Paths.AppDataThenName & "GloriaTools\MSL\"
    Private PathFrp As String = Paths.AppDataThenName & "GloriaTools\Frp\"
    Private PathServers As String = ""
    Private PathSettings As String = ""

    Private Instances As New List(Of MslInstance)
    Private SelectedInst As MslInstance = Nothing
    Private ProcMsl As Process = Nothing
    Private RunningInst As MslInstance = Nothing
    Private SwMslIn As StreamWriter = Nothing
    Private ProcMap As Process = Nothing
    Private ManifestJson As String = ""

    Private CurrentMslPanel As FrameworkElement = Nothing

    '日志缓冲：后台高频输出时先写入缓冲区，由定时器批量刷新到 UI，避免每行都跨线程刷新造成卡顿
    Private ConsBuf As New StringBuilder(), LogBuf As New StringBuilder()
    Private ConsLock As New Object()
    Private FlushTimer As DispatcherTimer = Nothing

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
        '初始化默认子页（主页）显示，加载阶段不做动画，避免与页面进入动画冲突
        PanHome.Visibility = Visibility.Visible
        PanList.Visibility = Visibility.Collapsed
        PanCreate.Visibility = Visibility.Collapsed
        PanConfig.Visibility = Visibility.Collapsed
        PanLog.Visibility = Visibility.Collapsed
        PanPanel.Visibility = Visibility.Collapsed
        PanMap.Visibility = Visibility.Collapsed
        PanSet.Visibility = Visibility.Collapsed
        CurrentMslPanel = PanHome
        BtnTabHome.Checked = True
        AttachMenu(BtnMslMore, AddressOf OnMslMenu)
        '定时器只在首次创建，避免每次进入页面重复创建叠加导致卡顿
        If FlushTimer Is Nothing Then
            FlushTimer = New DispatcherTimer() With {.Interval = TimeSpan.FromMilliseconds(120)}
            AddHandler FlushTimer.Tick, AddressOf FlushMslLogs
            FlushTimer.Start()
        End If
        LoadInstances()
        LoadSettings()
        UpdateCreateMode()
        RefreshMslState()
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
    '  子页切换（主页/服务器/新建/配置/日志/控制台/映射/设置）
    '===============================

    Private Sub TabMsl_Check(sender As Object, raiseByMouse As Boolean) Handles BtnTabHome.Check, BtnTabList.Check, BtnTabCreate.Check, BtnTabConfig.Check,
                                                                                 BtnTabLog.Check, BtnTabPanel.Check, BtnTabMap.Check, BtnTabSet.Check
        Dim T As FrameworkElement
        If BtnTabHome.Checked Then
            T = PanHome
        ElseIf BtnTabList.Checked Then
            T = PanList
        ElseIf BtnTabCreate.Checked Then
            T = PanCreate
        ElseIf BtnTabConfig.Checked Then
            T = PanConfig
        ElseIf BtnTabLog.Checked Then
            T = PanLog
        ElseIf BtnTabPanel.Checked Then
            T = PanPanel
        ElseIf BtnTabMap.Checked Then
            T = PanMap
        Else
            T = PanSet
        End If
        SwitchMslTab(T)
    End Sub

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
    Private Sub BtnListNew_Click() Handles BtnListNew.Click
        BtnTabCreate.Checked = True
    End Sub
    Private Sub BtnListRefresh_Click() Handles BtnListRefresh.Click
        LoadInstances()
        Hint("实例列表已刷新。", HintType.Blue)
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
                        .Args = If(O("Args") Is Nothing, "", O("Args").ToString())
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
                Arr.Add(New JObject From {{"Name", I.Name}, {"CorePath", I.CorePath}, {"Java", I.Java}, {"Memory", I.Memory}, {"Args", I.Args}})
            Next
            File.WriteAllText(PathServers & "servers.json", Arr.ToString(Formatting.Indented), Encoding.UTF8)
        Catch ex As Exception
            LogMsl("保存实例列表失败：" & ex.Message)
        End Try
    End Sub

    Private Sub RefreshInstanceList()
        PanInstanceList.Children.Clear()
        LabListEmpty.Visibility = If(Instances.Count = 0, Visibility.Visible, Visibility.Collapsed)
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
            Dim BCfg As New MyButton With {.Text = "配置", .Padding = New Thickness(12, 5, 12, 5), .Margin = New Thickness(0, 0, 8, 0)}
            AddHandler BCfg.Click, Sub()
                                       SelectedInst = Inst
                                       SetConfigTarget()
                                       BtnTabConfig.Checked = True
                                   End Sub
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
                                       End Sub)
                           Catch ex As Exception
                               RunInUi(Sub()
                                           LogMsl("核心下载失败：" & ex.Message)
                                           Hint("核心下载失败，请查看日志输出。", HintType.Red)
                                           BtnCreateDownload.IsEnabled = True
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
            LogMsl("正在获取 Fabric 加载器信息……")
            Dim Loaders = JArray.Parse(Client.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader").GetAwaiter().GetResult())
            Dim Loader As String = Loaders(0)("version").ToString()
            Dim Installers = JArray.Parse(Client.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer").GetAwaiter().GetResult())
            Dim Inst As String = Installers(0)("version").ToString()
            Dim Url As String = $"https://meta.fabricmc.net/v2/versions/loader/{Game}/{Loader}/{Inst}/server/jar"
            Dim Dest As String = DestDir & "fabric-server-" & Game & ".jar"
            LogMsl("开始下载 Fabric 服务端（游戏版本 " & Game & "，加载器 " & Loader & "）……")
            DownloadFile(Url, Dest)
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
            LogMsl("正在获取基岩版服务端（BDS）下载地址……")
            Dim Html As String = Client.GetStringAsync("https://www.minecraft.net/en-us/download/server/bedrock").GetAwaiter().GetResult()
            Dim M As Match = Regex.Match(Html, "https://[^""' ]*bin-win/bedrock-server-[\d.]+\.zip")
            If Not M.Success Then Throw New Exception("未能从官网解析到 BDS 下载地址（页面结构可能已变化）")
            Dim ZipPath As String = DestDir & "bedrock-server.zip"
            LogMsl("开始下载 BDS：" & M.Value)
            DownloadFile(M.Value, ZipPath)
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
            DownloadFile(DlUrl, Dest)
            Return Dest
        End If
    End Function

    'PaperMC 系（paper/velocity）：取最新版本最新构建
    Private Function DownloadPaperLike(Project As String, Ver As String, DestDir As String, Label As String) As String
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
        DownloadFile(Url, Dest)
        Return Dest
    End Function

    'Forge / NeoForge：下载安装器并静默安装到 DestDir，返回 run.bat
    Private Function InstallForgeLike(Kind As String, Ver As String, DestDir As String) As String
        Dim InstallerUrl As String = "", FullVer As String = ""
        If Kind = "forge" Then
            LogMsl("正在获取 Forge 推荐版本……")
            Dim Promo As JObject = JObject.Parse(Client.GetStringAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json").GetAwaiter().GetResult())
            Dim Promos As JObject = Promo("promos")
            Dim Game As String = If(Ver = "", LatestVanilla(), Ver)
            FullVer = If(Promos(Game & "-recommended") IsNot Nothing, Promos(Game & "-recommended").ToString(), If(Promos(Game & "-latest") IsNot Nothing, Promos(Game & "-latest").ToString(), ""))
            If FullVer = "" Then FullVer = Promos("1.20.1-recommended").ToString() '回退到长期支持版
            InstallerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{Game}-{FullVer}/forge-{Game}-{FullVer}-installer.jar"
        Else
            LogMsl("正在获取 NeoForge 最新版本……")
            Dim Vers As JArray = JArray.Parse(Client.GetStringAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge").GetAwaiter().GetResult())("versions")
            FullVer = Vers(Vers.Count - 1).ToString()
            InstallerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{FullVer}/neoforge-{FullVer}-installer.jar"
        End If
        Dim InstJar As String = DestDir & "installer.jar"
        LogMsl("开始下载安装器（" & FullVer & "）……")
        DownloadFile(InstallerUrl, InstJar)
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
            .Args = ""
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
        Hint("实例创建成功！可在「配置」页完善开服设置。", HintType.Green)
        BtnTabList.Checked = True
    End Sub

    '===============================
    '  服务器配置
    '===============================

    Private Sub SetConfigTarget()
        If SelectedInst Is Nothing Then
            LabCfgTarget.Text = "（未选择实例）"
            Return
        End If
        LabCfgTarget.Text = "（当前：" & SelectedInst.Name & "）"
        TxtCfgJava.Text = SelectedInst.Java
        TxtCfgArgs.Text = SelectedInst.Args
        SelectComboByText(ComboCfgMemory, SelectedInst.Memory)
        LoadProps()
        LoadLists()
    End Sub

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
            Hint("该实例还没有 server.properties（首次启动后生成），保存将创建新文件。", HintType.Blue)
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
                If Inst.Args <> "" Then Args &= Inst.Args & " "
                Args &= "-jar """ & Inst.CorePath & """ nogui"
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
            ProcMsl = New Process With {.StartInfo = Info, .EnableRaisingEvents = True}
            AddHandler ProcMsl.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then ConsMsl(e.Data)
            AddHandler ProcMsl.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then ConsMsl(e.Data)
            AddHandler ProcMsl.Exited, Sub(s, e)
                                           SwMslIn = Nothing
                                           RunInUi(Sub() ConsMsl("[实例 " & RunningInst.Name & " 已退出]"))
                                           RunInUi(AddressOf RefreshMslState)
                                       End Sub
            ProcMsl.Start()
            SwMslIn = ProcMsl.StandardInput
            ProcMsl.BeginOutputReadLine()
            ProcMsl.BeginErrorReadLine()
            RunningInst = Inst
            LogMsl("已启动实例：" & Inst.Name)
            LogMsl("启动命令：" & Info.FileName & " " & Info.Arguments)
            ConsMsl("[GloriaCloud] 实例 " & Inst.Name & " 已启动，可在下方输入指令。")
            Hint("实例已启动！切换到「控制台」查看输出。", HintType.Green)
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
        Try
            If Force Then
                'run.bat 方式启动时需结束整棵进程树（cmd + java）
                If InstCoreIsBat() Then
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
                Else
                    ProcMsl.Kill()
                End If
            End If
        Catch ex As Exception
            LogMsl("停止失败：" & ex.Message)
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
        Else
            LabMslConsState.Text = "● 已停止"
            LabMslConsState.Foreground = New SolidColorBrush(Color.FromRgb(150, 150, 150))
            LabHomeState.Text = "未运行"
            LabHomeRunning.Text = "—"
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
                                           RunInUi(Sub() LabMapState.Text = "映射未运行")
                                       End Sub
            ProcMap.Start()
            ProcMap.BeginOutputReadLine()
            ProcMap.BeginErrorReadLine()
            LabMapState.Text = "映射运行中"
            LogMsl("映射配置已生成：" & Ini)
            ConsMsl("[GloriaCloud] 映射已启动：本地 " & LocalPort & " → 远程 " & RemotePort)
            Hint("映射已启动！玩家可通过 服务器地址:远程端口 连接。", HintType.Green)
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

    '===============================
    '  设置
    '===============================

    Private Sub LoadSettings()
        Try
            If File.Exists(PathSettings) Then
                Dim O = JObject.Parse(File.ReadAllText(PathSettings))
                TxtSetJava.Text = If(O("Java") Is Nothing, "", O("Java").ToString())
            End If
        Catch
        End Try
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
    '  下载辅助
    '===============================

    Private Sub DownloadFile(Url As String, Dest As String)
        Directory.CreateDirectory(Path.GetDirectoryName(Dest))
        Using Resp = Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
            Resp.EnsureSuccessStatusCode()
            Using FS = File.Create(Dest)
                Using Stream = Resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    Dim Buf(8191) As Byte
                    Dim N As Integer
                    Do
                        N = Stream.Read(Buf, 0, Buf.Length)
                        If N <= 0 Then Exit Do
                        FS.Write(Buf, 0, N)
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
