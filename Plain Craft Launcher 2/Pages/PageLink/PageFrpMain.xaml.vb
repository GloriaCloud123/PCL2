Imports System.IO
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Diagnostics
Imports Microsoft.Win32
Imports PCL

Public Class PageFrpMain

    '===============================
    '  字段与初始化
    '===============================

    Private Shared ReadOnly Client As New HttpClient()
    Private PathFrp As String = Paths.AppDataThenName & "GloriaTools\Frp\"
    Private FrpExePath As String = ""
    Private FrpConfigPath As String = ""
    Private FrpMode As String = ""
    Private ProcFrpc As Process = Nothing

    Private Sub Init() Handles Me.Loaded
        BtnFrpTabServer.Checked = True '确保默认服务方标签页被正确高亮并显示
        AttachMenu(BtnFrpMore, AddressOf OnFrpMenu)
        RefreshFrpState()
        RunInNewThread(AddressOf CheckFrpcThread)
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
    '  子功能切换（服务方 / 日志输出 / 控制台）
    '===============================

    Private Sub TabFrp_Check(sender As Object, raiseByMouse As Boolean) Handles BtnFrpTabServer.Check, BtnFrpTabLog.Check, BtnFrpTabPanel.Check
        PanFrpServer.Visibility = If(BtnFrpTabServer.Checked, Visibility.Visible, Visibility.Collapsed)
        PanFrpLog.Visibility = If(BtnFrpTabLog.Checked, Visibility.Visible, Visibility.Collapsed)
        PanFrpPanel.Visibility = If(BtnFrpTabPanel.Checked, Visibility.Visible, Visibility.Collapsed)
    End Sub

    '===============================
    '  frpc 客户端
    '===============================

    Private Sub CheckFrpcThread()
        Try
            Dim Found = FindFile(PathFrp, "frpc.exe")
            RunInUi(Sub()
                        If Found IsNot Nothing Then
                            FrpExePath = Found
                            LabFrpFrpc.Text = Found
                            LabFrpFrpc.ToolTip = Found
                        Else
                            LabFrpFrpc.Text = "尚未下载（点击自动下载 frpc）"
                        End If
                    End Sub)
        Catch
        End Try
    End Sub

    Private Sub BtnFrpDownload_Click() Handles BtnFrpDownload.Click
        BtnFrpDownload.IsEnabled = False
        RunInNewThread(Sub()
                           Try
                               LogFrp("正在获取 frpc 下载地址……")
                               Dim Url As String = GetFrpcUrlAsync().GetAwaiter().GetResult()
                               LogFrp("下载地址：" & Url)
                               Directory.CreateDirectory(PathFrp)
                               Dim Temp As String = PathFrp & "frp_dl.zip"
                               If File.Exists(Temp) Then File.Delete(Temp)
                               DownloadFile(Url, Temp)
                               LogFrp("下载完成，正在解压……")
                               ExtractZip(Temp, PathFrp)
                               File.Delete(Temp)
                               Dim Found = FindFile(PathFrp, "frpc.exe")
                               If Found Is Nothing Then Throw New Exception("解压后未找到 frpc.exe")
                               RunInUi(Sub()
                                           FrpExePath = Found
                                           LabFrpFrpc.Text = Found
                                           LabFrpFrpc.ToolTip = Found
                                           LogFrp("frpc 已就绪：" & Found)
                                           Hint("frpc 下载完成！", HintType.Green)
                                           BtnFrpDownload.IsEnabled = True
                                       End Sub)
                           Catch ex As Exception
                               RunInUi(Sub()
                                           LogFrp("下载失败：" & ex.Message)
                                           Hint("frpc 下载失败，请查看日志输出。", HintType.Red)
                                           BtnFrpDownload.IsEnabled = True
                                       End Sub)
                           End Try
                       End Sub)
    End Sub

    '===============================
    '  配置来源
    '===============================

    Private Sub CardFrpIni_Click() Handles CardFrpIni.MouseLeftButtonUp
        FrpMode = "ini"
        SpFrpIniInner.Opacity = 1
        SpFrpPasteInner.Opacity = 0.55
        PanFrpIni.Visibility = Visibility.Visible
        TxtFrpPaste.Visibility = Visibility.Collapsed
    End Sub

    Private Sub CardFrpPaste_Click() Handles CardFrpPaste.MouseLeftButtonUp
        FrpMode = "paste"
        SpFrpPasteInner.Opacity = 1
        SpFrpIniInner.Opacity = 0.55
        PanFrpIni.Visibility = Visibility.Collapsed
        TxtFrpPaste.Visibility = Visibility.Visible
    End Sub

    Private Sub BtnFrpChooseFile_Click() Handles BtnFrpChooseFile.Click
        Dim Dlg As New OpenFileDialog With {
            .Title = "选择 frpc 配置文件",
            .Filter = "frpc 配置文件 (*.ini;*.toml;*.txt)|*.ini;*.toml;*.txt|所有文件 (*.*)|*.*"
        }
        If Dlg.ShowDialog() <> True Then Return
        FrpConfigPath = Dlg.FileName
        LabFrpFilePath.Text = FrpConfigPath
        LabFrpFilePath.ToolTip = FrpConfigPath
        LogFrp("已选择配置文件：" & FrpConfigPath)
    End Sub

    '===============================
    '  启动 / 停止
    '===============================

    Private Function IsRunning() As Boolean
        Return ProcFrpc IsNot Nothing AndAlso Not ProcFrpc.HasExited
    End Function

    Private Sub BtnFrpStart_Click() Handles BtnFrpStart.Click
        If IsRunning() Then
            Hint("frpc 已在运行！", HintType.Blue) : Return
        End If
        If String.IsNullOrEmpty(FrpExePath) OrElse Not File.Exists(FrpExePath) Then
            Hint("请先在""服务方""中下载 frpc 客户端！", HintType.Red) : Return
        End If
        If String.IsNullOrEmpty(FrpMode) Then
            Hint("请先选择配置来源：导入 ini 配置文件 或 手动粘贴 txt 代码！", HintType.Red) : Return
        End If
        Try
            Dim Config = GetFrpcConfigFile()
            LogFrp("使用配置：" & Config)
            Dim Info = New ProcessStartInfo With {
                .FileName = FrpExePath,
                .Arguments = "-c """ & Config & """",
                .WorkingDirectory = Path.GetDirectoryName(Config),
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            ProcFrpc = New Process With {.StartInfo = Info, .EnableRaisingEvents = True}
            AddHandler ProcFrpc.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then ConsFrp(e.Data)
            AddHandler ProcFrpc.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then ConsFrp(e.Data)
            AddHandler ProcFrpc.Exited, Sub(s, e)
                                            RunInUi(Sub() ConsFrp("[frpc 已退出]"))
                                            RunInUi(AddressOf RefreshFrpState)
                                        End Sub
            ProcFrpc.Start()
            ProcFrpc.BeginOutputReadLine()
            ProcFrpc.BeginErrorReadLine()
            LogFrp("frpc 已启动，正在建立映射……")
            ConsFrp("[GloriaCloud] frpc 已启动，等待映射结果……")
            Hint("frpc 已启动！", HintType.Green)
        Catch ex As Exception
            LogFrp("启动失败：" & ex.Message)
            Hint("frpc 启动失败：" & ex.Message, HintType.Red)
        End Try
        RefreshFrpState()
    End Sub

    Private Sub BtnFrpStop_Click() Handles BtnFrpStop.Click, BtnFrpStop2.Click
        StopFrpc()
    End Sub

    Private Sub StopFrpc()
        If Not IsRunning() Then
            Hint("frpc 并未运行。", HintType.Blue) : Return
        End If
        Try
            ProcFrpc.Kill()
            LogFrp("已停止 frpc。")
            ConsFrp("[frpc 已被停止]")
        Catch ex As Exception
            LogFrp("停止失败：" & ex.Message)
        End Try
    End Sub

    Private Sub BtnFrpRestart_Click() Handles BtnFrpRestart.Click
        If IsRunning() Then StopFrpc()
        BtnFrpStart_Click()
    End Sub

    Private Function GetFrpcConfigFile() As String
        If FrpMode = "ini" Then
            If String.IsNullOrEmpty(FrpConfigPath) OrElse Not File.Exists(FrpConfigPath) Then
                Throw New Exception("请先选择 ini / toml 配置文件。")
            End If
            Return FrpConfigPath
        Else
            Dim Text = TxtFrpPaste.Text
            If String.IsNullOrWhiteSpace(Text) Then Throw New Exception("请先在文本框中粘贴 frpc 配置。")
            Dim Ext = If(Text.Contains("[["), ".toml", ".ini")
            Dim F = PathFrp & "frpc_config" & Ext
            Directory.CreateDirectory(PathFrp)
            File.WriteAllText(F, Text, Encoding.UTF8)
            Return F
        End If
    End Function

    '===============================
    '  控制台 / 状态 / 日志
    '===============================

    Private Sub BtnFrpMore_Click() Handles BtnFrpMore.Click
        If BtnFrpMore.ContextMenu IsNot Nothing Then
            BtnFrpMore.ContextMenu.PlacementTarget = BtnFrpMore
            BtnFrpMore.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top
            BtnFrpMore.ContextMenu.IsOpen = True
        End If
    End Sub

    Private Sub OnFrpMenu(sender As Object, e As RoutedEventArgs)
        Dim TagVal = CType(sender, MyMenuItem).Tag
        Select Case If(TagVal Is Nothing, "", TagVal.ToString())
            Case "clear"
                TxtFrpConsole.Clear()
            Case "folder"
                Try
                    Directory.CreateDirectory(PathFrp)
                    Process.Start(New ProcessStartInfo(PathFrp) With {.UseShellExecute = True})
                Catch ex As Exception
                    Hint("打开目录失败：" & ex.Message, HintType.Red)
                End Try
        End Select
    End Sub

    Private Sub RefreshFrpState()
        If IsRunning() Then
            LabFrpState.Text = "状态：映射中（PID " & ProcFrpc.Id & "）"
            LabFrpConsState.Text = "● 映射中"
            LabFrpConsState.Foreground = New SolidColorBrush(Color.FromRgb(76, 200, 90))
        Else
            LabFrpState.Text = "状态：未运行"
            LabFrpConsState.Text = "● 已停止"
            LabFrpConsState.Foreground = New SolidColorBrush(Color.FromRgb(150, 150, 150))
        End If
        Dim Info As String = ""
        If FrpMode = "ini" AndAlso FrpConfigPath <> "" Then Info = FrpConfigPath
        If FrpMode = "paste" Then Info = "粘贴文本（临时配置文件）"
        LabFrpConsInfo.Text = Info
    End Sub

    Private Sub BtnFrpLogClear_Click() Handles BtnFrpLogClear.Click
        TxtFrpLog.Clear()
    End Sub

    Private Sub LogFrp(Line As String)
        RunInUi(Sub()
                    TxtFrpLog.AppendText("[" & Date.Now.ToString("HH:mm:ss") & "] " & Line & vbCrLf)
                    TxtFrpLog.ScrollToEnd()
                End Sub)
    End Sub

    Private Sub ConsFrp(Line As String)
        RunInUi(Sub()
                    TxtFrpConsole.AppendText(Line & vbCrLf)
                    TxtFrpConsole.ScrollToEnd()
                End Sub)
    End Sub

    ''' <summary>
    ''' 关闭启动器时强制结束 frpc 进程。
    ''' </summary>
    Public Sub KillOnExit()
        Try
            If ProcFrpc IsNot Nothing AndAlso Not ProcFrpc.HasExited Then ProcFrpc.Kill()
        Catch
        End Try
        ProcFrpc = Nothing
    End Sub

    '===============================
    '  下载 / 解压 辅助
    '===============================

    Private Async Function GetFrpcUrlAsync() As Task(Of String)
        Dim Json = Await Client.GetStringAsync("https://api.github.com/repos/fatedier/frp/releases/latest")
        Dim M = Regex.Match(Json, """browser_download_url""\s*:\s*""(?<u>[^""]*windows_amd64[^""]*\.zip)""")
        If M.Success Then Return M.Groups("u").Value.Replace("\/", "/")
        Throw New Exception("未能获取 frpc 下载地址（windows_amd64）。")
    End Function

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

    Private Sub ExtractZip(Source As String, TargetDir As String)
        Directory.CreateDirectory(TargetDir)
        System.IO.Compression.ZipFile.ExtractToDirectory(Source, TargetDir)
    End Sub

    Private Function FindFile(Root As String, Name As String) As String
        If Not Directory.Exists(Root) Then Return Nothing
        For Each F In Directory.EnumerateFiles(Root, Name, SearchOption.AllDirectories)
            Return F
        Next
        Return Nothing
    End Function

End Class
