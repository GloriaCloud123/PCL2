Imports System.IO
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Diagnostics
Imports System.Windows.Threading
Imports Microsoft.Win32
Imports Newtonsoft.Json.Linq
Imports PCL

Public Class PageMslMain

    '===============================
    '  字段与初始化
    '===============================

    Private Shared ReadOnly Client As New HttpClient()
    Private PathMsl As String = Paths.AppDataThenName & "GloriaTools\MSL\"
    Private MslCorePath As String = ""
    Private ProcMsl As Process = Nothing
    Private SwMslIn As StreamWriter = Nothing
    Private ManifestJson As String = ""

    Private CurrentMslPanel As FrameworkElement = Nothing

    '日志缓冲：后台高频输出时先写入缓冲区，由定时器批量刷新到 UI，避免每行都跨线程刷新造成卡顿
    Private ConsBuf As New StringBuilder(), LogBuf As New StringBuilder()
    Private ConsLock As New Object()
    Private FlushTimer As DispatcherTimer = Nothing

    Private Sub Init() Handles Me.Loaded
        '初始化默认子页（服务方）显示，加载阶段不做动画，避免与页面进入动画冲突
        PanServer.Visibility = Visibility.Visible
        PanLog.Visibility = Visibility.Collapsed
        PanPanel.Visibility = Visibility.Collapsed
        CurrentMslPanel = PanServer
        BtnTabServer.Checked = True '确保默认服务方标签页被正确高亮并显示
        AttachMenu(BtnMslMore, AddressOf OnMslMenu)
        RefreshMslState()
        '定时器只在首次创建，避免每次进入页面重复创建叠加导致卡顿
        If FlushTimer Is Nothing Then
            FlushTimer = New DispatcherTimer() With {.Interval = TimeSpan.FromMilliseconds(120)}
            AddHandler FlushTimer.Tick, AddressOf FlushMslLogs
            FlushTimer.Start()
        End If
        '仅在尚未加载过版本列表时才联网拉取，避免重复请求
        If ComboMslVersion.Items.Count = 0 Then RunInNewThread(AddressOf LoadVersionsThread)
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

    Private Sub AttachMenu(Btn As MyButton, H As RoutedEventHandler)
        If Btn.ContextMenu IsNot Nothing Then
            For Each it As Object In Btn.ContextMenu.Items
                Dim mi As MyMenuItem = TryCast(it, MyMenuItem)
                If mi IsNot Nothing Then AddHandler mi.Click, H
            Next
        End If
    End Sub

    '===============================
    '  子功能切换（服务方 / 日志输出 / 控制台）
    '===============================

    Private Sub TabMsl_Check(sender As Object, raiseByMouse As Boolean) Handles BtnTabServer.Check, BtnTabLog.Check, BtnTabPanel.Check
        SwitchMslTab(If(BtnTabServer.Checked, PanServer, If(BtnTabLog.Checked, PanLog, PanPanel)))
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

    '===============================
    '  服务端核心
    '===============================

    Private Sub BtnMslCoreSelect_Click() Handles BtnMslCoreSelect.Click
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择服务端核心",
            .Filter = "服务端核心 (*.jar)|*.jar|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return
        MslCorePath = Dlg.FileName
        LabMslCore.Text = MslCorePath
        LabMslCore.ToolTip = MslCorePath
        LogMsl("已选择核心文件：" & MslCorePath)
    End Sub

    Private Sub BtnMslRefresh_Click() Handles BtnMslRefresh.Click
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
                        ComboMslVersion.Items.Clear()
                        For Each N As String In Names
                            ComboMslVersion.Items.Add(N)
                        Next
                        If ComboMslVersion.Items.Count > 0 Then ComboMslVersion.SelectedIndex = 0
                    End Sub)
            LogMsl("版本列表获取完成，共 " & Names.Count & " 个正式版。")
        Catch ex As Exception
            RunInUi(Sub() LogMsl("版本列表获取失败：" & ex.Message))
        End Try
    End Sub

    Private Sub BtnMslCoreDownload_Click() Handles BtnMslCoreDownload.Click
        If ComboMslVersion.SelectedItem Is Nothing Then
            Hint("请先刷新并选择一个版本！", HintType.Red) : Return
        End If
        Dim Ver As String = ComboMslVersion.SelectedItem.ToString()
        BtnMslCoreDownload.IsEnabled = False
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
                               Dim Dir As String = PathMsl & "Server\" & Ver & "\"
                               Dim Dest As String = Dir & "server.jar"
                               Directory.CreateDirectory(Dir)
                               LogMsl("开始下载服务端（" & Ver & "）……")
                               DownloadFile(DlUrl, Dest)
                               MslCorePath = Dest
                               RunInUi(Sub()
                                           LabMslCore.Text = Dest
                                           LabMslCore.ToolTip = Dest
                                           LabMslConsCore.Text = Dest
                                           LogMsl("服务端已就绪：" & Dest)
                                           Hint("服务端下载完成！", HintType.Green)
                                           BtnMslCoreDownload.IsEnabled = True
                                       End Sub)
                           Catch ex As Exception
                               RunInUi(Sub()
                                           LogMsl("服务端下载失败：" & ex.Message)
                                           Hint("服务端下载失败，请查看日志输出。", HintType.Red)
                                           BtnMslCoreDownload.IsEnabled = True
                                       End Sub)
                           End Try
                       End Sub)
    End Sub

    '===============================
    '  运行环境
    '===============================

    Private Sub BtnMslJavaSelect_Click() Handles BtnMslJavaSelect.Click
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择 Java 运行时",
            .Filter = "Java 运行时 (*.exe)|*.exe|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return
        TxtMslJava.Text = Dlg.FileName
    End Sub

    '===============================
    '  启动 / 停止
    '===============================

    Private Function IsRunning() As Boolean
        Return ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited
    End Function

    Private Sub BtnMslStart_Click() Handles BtnMslStart.Click
        If IsRunning() Then
            Hint("服务器已在运行中！", HintType.Blue) : Return
        End If
        If String.IsNullOrEmpty(MslCorePath) OrElse Not File.Exists(MslCorePath) Then
            Hint("请先选择或下载服务端核心！", HintType.Red) : Return
        End If
        Try
            Dim Java As String = TxtMslJava.Text.Trim()
            If Java = "" Then Java = "java"
            Dim Args As String = ""
            Dim MemItem = TryCast(ComboMslMemory.SelectedItem, ComboBoxItem)
            Dim Mem As String = If(MemItem IsNot Nothing, MemItem.Content.ToString(), "自动")
            If Mem <> "自动" Then Args &= "-Xmx" & Mem.Replace(" ", "") & " "
            Dim Extra As String = TxtMslArgs.Text.Trim()
            If Extra <> "" Then Args &= Extra & " "
            Args &= "-jar """ & MslCorePath & """ nogui"
            Dim Info = New ProcessStartInfo With {
                .FileName = Java,
                .Arguments = Args,
                .WorkingDirectory = Path.GetDirectoryName(MslCorePath),
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
                                           RunInUi(Sub() ConsMsl("[服务器已退出]"))
                                           RunInUi(AddressOf RefreshMslState)
                                       End Sub
            ProcMsl.Start()
            SwMslIn = ProcMsl.StandardInput
            ProcMsl.BeginOutputReadLine()
            ProcMsl.BeginErrorReadLine()
            LogMsl("已启动服务器：" & MslCorePath)
            LogMsl("启动命令：" & Java & " " & Args)
            ConsMsl("[GloriaCloud] 服务器已启动，可在下方输入指令。")
            Hint("服务器已启动！", HintType.Green)
        Catch ex As Exception
            LogMsl("启动失败：" & ex.Message)
            Hint("服务器启动失败：" & ex.Message, HintType.Red)
        End Try
        RefreshMslState()
    End Sub

    Private Sub BtnMslStop_Click() Handles BtnMslStop.Click
        StopServer(False)
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
                BtnMslStart_Click()
            Case "folder"
                Try
                    Dim Dir As String = If(String.IsNullOrEmpty(MslCorePath), PathMsl, Path.GetDirectoryName(MslCorePath))
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
        If IsRunning() Then
            LabMslState.Text = "状态：运行中（PID " & ProcMsl.Id & "）"
            LabMslConsState.Text = "● 运行中"
            LabMslConsState.Foreground = New SolidColorBrush(Color.FromRgb(76, 200, 90))
        Else
            LabMslState.Text = "状态：未运行"
            LabMslConsState.Text = "● 已停止"
            LabMslConsState.Foreground = New SolidColorBrush(Color.FromRgb(150, 150, 150))
        End If
        If LabMslConsCore.Text = "" AndAlso MslCorePath <> "" Then LabMslConsCore.Text = MslCorePath
    End Sub

    Private Sub BtnMslLogClear_Click() Handles BtnMslLogClear.Click
        TxtMslLog.Clear()
        SyncLock ConsLock : LogBuf.Clear() : End SyncLock
    End Sub

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

    ''' <summary>
    ''' 关闭启动器时强制结束服务器进程。
    ''' </summary>
    Public Sub KillOnExit()
        Try
            If ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited Then ProcMsl.Kill()
        Catch
        End Try
        ProcMsl = Nothing
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

End Class
