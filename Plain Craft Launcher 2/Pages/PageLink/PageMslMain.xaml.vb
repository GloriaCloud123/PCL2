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

    Private Sub Init() Handles Me.Loaded
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
        If Ver = "" Then
            Hint("请先刷新并选择一个版本！", HintType.Red) : Return
        End If
        BtnCreateDownload.IsEnabled = False
        RunInNewThread(Sub()
                           Try
                               If String.IsNullOrEmpty(ManifestJson) Then Throw New Exception("版本列表尚未加载，请先刷新列表")
                               LogMsl("正在获取版本信息：" & Ver & " ……")
                               Dim Job As JObject = JObject.Parse(ManifestJson)
                               Dim Url As String = ""
                               For Each v As JToken In Job("versions")
                                   If v("id") IsNot Nothing AndAlso v("id").ToString() = Ver Then
                                       Url = v("url").ToString()
                                       Exit For
                                   End If
                               Next
                               If Url = "" Then Throw New Exception("未找到该版本的信息")
                               Dim VJson As String = Client.GetStringAsync(Url).GetAwaiter().GetResult()
                               Dim VJob As JObject = JObject.Parse(VJson)
                               Dim DlUrl As String = VJob("downloads")("server")("url").ToString()
                               Dim Dir As String = PathServers & Ver & "\"
                               Dim Dest As String = Dir & "server.jar"
                               Directory.CreateDirectory(Dir)
                               LogMsl("开始下载服务端核心（" & Ver & "）……")
                               DownloadFile(DlUrl, Dest)
                               RunInUi(Sub()
                                           TxtCreateName.Tag = Dest
                                           LabCreateCore.Text = Dest
                                           LogMsl("核心已就绪：" & Dest)
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

    Private Sub BtnCreateOk_Click() Handles BtnCreateOk.Click
        Dim Name As String = TxtCreateName.Text.Trim()
        If Name = "" Then
            Hint("请先填写实例名称！", HintType.Red) : Return
        End If
        Dim Core = If(TxtCreateName.Tag Is Nothing, "", TxtCreateName.Tag.ToString())
        If Core = "" OrElse Not File.Exists(Core) Then
            Hint("请先下载核心或选择本地 jar 文件！", HintType.Red) : Return
        End If
        If Instances.Any(Function(x) x.Name = Name) Then
            Hint("已存在同名实例，请换一个名称。", HintType.Red) : Return
        End If
        '把核心复制进实例目录，保证每个实例独立
        Dim Dir As String = PathServers & Name & "\"
        Dim Dest As String = Dir & Path.GetFileName(Core)
        Try
            Directory.CreateDirectory(Dir)
            If Path.GetDirectoryName(Core) <> Dir Then File.Copy(Core, Dest, True)
        Catch ex As Exception
            Hint("创建实例目录失败：" & ex.Message, HintType.Red) : Return
        End Try
        Dim Inst As New MslInstance With {
            .Name = Name,
            .CorePath = Dest,
            .Java = TxtCreateJava.Text.Trim(),
            .Memory = ComboText(ComboCreateMemory),
            .Args = ""
        }
        Instances.Add(Inst)
        SaveInstances()
        RefreshInstanceList()
        TxtCreateName.Text = ""
        TxtCreateName.Tag = Nothing
        LabCreateCore.Text = "未选择（将使用在线下载的核心）"
        LogMsl("已创建实例：" & Name & "（" & Dest & "）")
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

    Private Function IsRunning() As Boolean
        Return ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited
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
            Dim Java As String = If(Inst.Java <> "", Inst.Java, TxtSetJava.Text.Trim())
            If Java = "" Then Java = "java"
            Dim Args As String = ""
            If Inst.Memory <> "自动" Then Args &= "-Xmx" & Inst.Memory.Replace(" ", "") & " "
            If Inst.Args <> "" Then Args &= Inst.Args & " "
            Args &= "-jar """ & Inst.CorePath & """ nogui"
            Dim Info = New ProcessStartInfo With {
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
            LogMsl("启动命令：" & Java & " " & Args)
            ConsMsl("[GloriaCloud] 实例 " & Inst.Name & " 已启动，可在下方输入指令。")
            Hint("实例已启动！切换到「控制台」查看输出。", HintType.Green)
        Catch ex As Exception
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
                ProcMsl.Kill()
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
        Return ProcMap IsNot Nothing AndAlso Not ProcMap.HasExited
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
