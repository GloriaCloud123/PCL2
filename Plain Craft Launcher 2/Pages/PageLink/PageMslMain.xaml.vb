Imports System.IO
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Diagnostics
Imports PCL

Public Class PageMslMain

    '===============================
    '  字段与初始化
    '===============================

    Private Shared ReadOnly Client As New HttpClient()
    Private PathMsl As String = Paths.AppDataThenName & "GloriaTools\MSL\"
    Private MslExePath As String = Nothing
    Private ProcMsl As Process = Nothing

    Private Sub Init() Handles Me.Loaded
        LabMslPath.Text = PathMsl
        BtnTabServer.Checked = True '确保默认“服务方”标签页被正确高亮并显示
        RefreshMslPanel()
    End Sub

    '===============================
    '  子功能切换（服务方 / 日志输出 / 服务器面板）
    '===============================

    Private Sub TabMsl_Check(sender As Object, raiseByMouse As Boolean) Handles BtnTabServer.Check, BtnTabLog.Check, BtnTabPanel.Check
        PanServer.Visibility = If(BtnTabServer.Checked, Visibility.Visible, Visibility.Collapsed)
        PanLog.Visibility = If(BtnTabLog.Checked, Visibility.Visible, Visibility.Collapsed)
        PanPanel.Visibility = If(BtnTabPanel.Checked, Visibility.Visible, Visibility.Collapsed)
    End Sub

    '===============================
    '  下载 MSL
    '===============================

    Private Async Sub BtnMslDownload_Click() Handles BtnMslDownload.Click
        BtnMslDownload.IsEnabled = False
        Try
            LogMsl("开始获取 MSL 下载地址……")
            Dim Url As String = Await GetMslUrlAsync()
            LogMsl("下载地址：" & Url)
            Directory.CreateDirectory(PathMsl)
            Dim Temp As String = PathMsl & "MSL_dl.tmp"
            If File.Exists(Temp) Then File.Delete(Temp)
            Await DownloadFileAsync(Url, Temp, Sub(d, t) RunInUi(Sub() LogMsl($"下载中… {d \ 1024} / {t \ 1024} KB")))
            LogMsl("下载完成，正在处理……")
            If IsZip(Temp) Then
                ExtractZip(Temp, PathMsl)
                File.Delete(Temp)
                Dim Exe = FindMainExe(PathMsl)
                If Exe Is Nothing Then Throw New Exception("解压后未找到 MSL 主程序，请手动到工具目录查看。")
                MslExePath = Exe
                LogMsl("已就绪：" & Exe)
                Hint("MSL 下载并解压完成！", HintType.Green)
            Else
                MslExePath = PathMsl & "MSL_setup.exe"
                If File.Exists(MslExePath) Then File.Delete(MslExePath)
                File.Move(Temp, MslExePath)
                LogMsl("已下载安装包：MSL_setup.exe。点击""启动 MSL""运行安装 / 启动。")
                Hint("MSL 下载完成（安装包）！", HintType.Green)
            End If
        Catch ex As Exception
            LogMsl("下载失败：" & ex.Message)
            Hint("MSL 下载失败，请查看日志。", HintType.Red)
        Finally
            BtnMslDownload.IsEnabled = True
            RunInUi(AddressOf RefreshMslPanel)
        End Try
    End Sub

    '===============================
    '  启动 / 停止
    '===============================

    Private Sub BtnMslStart_Click() Handles BtnMslStart.Click
        If ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited Then
            Hint("MSL 已在运行！", HintType.Blue) : Return
        End If
        Dim Exe = MslExePath
        If String.IsNullOrEmpty(Exe) OrElse Not File.Exists(Exe) Then
            Hint("请先在""服务方""中下载 MSL 工具！", HintType.Red) : Return
        End If
        Try
            Dim ServerName As String = If(String.IsNullOrWhiteSpace(TxtMslName.Text), "我的服务器", TxtMslName.Text)
            Dim ServerPort As String = If(String.IsNullOrWhiteSpace(TxtMslPort.Text), "25565", TxtMslPort.Text)
            LogMsl($"准备启动 MSL（名称：{ServerName}，端口：{ServerPort}）……")
            Dim Info = New ProcessStartInfo With {
                .FileName = Exe,
                .WorkingDirectory = PathMsl,
                .UseShellExecute = False,
                .CreateNoWindow = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }
            ProcMsl = New Process With {.StartInfo = Info, .EnableRaisingEvents = True}
            AddHandler ProcMsl.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then LogMsl(e.Data)
            AddHandler ProcMsl.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then LogMsl("[ERR] " & e.Data)
            AddHandler ProcMsl.Exited, Sub(s, e) RunInUi(Sub() RefreshMslPanel())
            ProcMsl.Start()
            ProcMsl.BeginOutputReadLine()
            ProcMsl.BeginErrorReadLine()
            LogMsl("已启动 MSL：" & Exe)
            Hint("MSL 已启动！", HintType.Green)
        Catch ex As Exception
            LogMsl("启动失败：" & ex.Message)
            Hint("MSL 启动失败：" & ex.Message, HintType.Red)
        End Try
        RunInUi(AddressOf RefreshMslPanel)
    End Sub

    Private Sub BtnMslStop_Click() Handles BtnMslStop.Click, BtnMslStop2.Click
        StopMsl()
    End Sub
    Private Sub StopMsl()
        Try
            If ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited Then
                ProcMsl.Kill()
                LogMsl("已停止 MSL。")
            Else
                LogMsl("MSL 并未运行。")
            End If
        Catch ex As Exception
            LogMsl("停止失败：" & ex.Message)
        Finally
            ProcMsl = Nothing
            RunInUi(AddressOf RefreshMslPanel)
        End Try
    End Sub

    ''' <summary>
    ''' 关闭启动时强制结束 MSL 进程。
    ''' </summary>
    Public Sub KillOnExit()
        Try
            If ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited Then ProcMsl.Kill()
        Catch
        End Try
        ProcMsl = Nothing
    End Sub

    Private Sub BtnMslOpenFolder_Click() Handles BtnMslOpenFolder.Click
        Try
            Directory.CreateDirectory(PathMsl)
            Process.Start(New ProcessStartInfo(PathMsl) With {.UseShellExecute = True})
        Catch ex As Exception
            LogMsl("打开目录失败：" & ex.Message)
        End Try
    End Sub
    Private Sub BtnMslLogClear_Click() Handles BtnMslLogClear.Click
        TxtMslLog.Clear()
    End Sub

    '===============================
    '  服务器面板
    '===============================

    Private Sub RefreshMslPanel()
        If ProcMsl IsNot Nothing AndAlso Not ProcMsl.HasExited Then
            LabMslStatus.Text = "运行中"
            LabMslPid.Text = ProcMsl.Id.ToString()
        Else
            LabMslStatus.Text = "未运行"
            LabMslPid.Text = "—"
        End If
        LabMslPath.Text = PathMsl
    End Sub

    '===============================
    '  日志
    '===============================

    Private Sub LogMsl(line As String)
        RunInUi(Sub()
                    TxtMslLog.AppendText(line & vbCrLf)
                    TxtMslLog.ScrollToEnd()
                End Sub)
    End Sub

    '===============================
    '  下载 / 解压 辅助
    '===============================

    Private Async Function DownloadFileAsync(Url As String, Dest As String, OnProgress As Action(Of Long, Long)) As Task
        Directory.CreateDirectory(Path.GetDirectoryName(Dest))
        Using Resp = Await Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead)
            Resp.EnsureSuccessStatusCode()
            Dim Total = Resp.Content.Headers.ContentLength
            Using FS = File.Create(Dest)
                Using Stream = Await Resp.Content.ReadAsStreamAsync()
                    Dim Buf(8191) As Byte
                    Dim Read As Integer, Done As Long = 0
                    Do While (Read = Await Stream.ReadAsync(Buf, 0, Buf.Length)) > 0
                        Await FS.WriteAsync(Buf, 0, Read)
                        Done += Read
                        If Total.HasValue Then OnProgress(Done, Total.Value)
                    Loop
                End Using
            End Using
        End Using
    End Function

    Private Async Function GetMslUrlAsync() As Task(Of String)
        '优先：GitHub Releases（第一个含有 exe/zip 资源的版本）
        Try
            Dim Json = Await Client.GetStringAsync("https://api.github.com/repos/MSLTeam/MSL/releases")
            Dim Matches = Regex.Matches(Json, """browser_download_url""\s*:\s*""(?<u>[^""]+\.(?:exe|zip))""")
            If Matches.Count > 0 Then Return Matches(0).Groups("u").Value.Replace("\/", "/")
        Catch ex As Exception
            LogMsl("GitHub 获取失败，改用官网直链：" & ex.Message)
        End Try
        '兜底：官网直链
        Return "https://api.mslmc.cn/v3/download/update?direct=true"
    End Function

    Private Function IsZip(Path As String) As Boolean
        Try
            Dim Head(3) As Byte
            Using FS = File.OpenRead(Path)
                If FS.Read(Head, 0, 4) < 4 Then Return False
            End Using
            Return Head(0) = &H50 AndAlso Head(1) = &H4B AndAlso Head(2) = &H3 AndAlso Head(3) = &H4
        Catch
            Return False
        End Try
    End Function

    Private Sub ExtractZip(Source As String, TargetDir As String)
        Directory.CreateDirectory(TargetDir)
        System.IO.Compression.ZipFile.ExtractToDirectory(Source, TargetDir)
    End Sub

    Private Function FindMainExe(Root As String) As String
        Dim First As String = Nothing
        For Each F In Directory.EnumerateFiles(Root, "*.exe", SearchOption.AllDirectories)
            If Path.GetFileName(F).IndexOf("MSL", StringComparison.OrdinalIgnoreCase) >= 0 Then Return F
            If First Is Nothing Then First = F
        Next
        Return First
    End Function

End Class
