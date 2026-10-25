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
    Private FrpExePath As String = Nothing
    Private FrpConfigPath As String = Nothing
    Private FrpMode As String = Nothing
    Private ProcFrpc As Process = Nothing

    Private Sub Init() Handles Me.Loaded
        LabFrpConfig.Text = "—"
        BtnFrpTabServer.Checked = True '确保默认“服务方”标签页被正确高亮并显示
        RefreshFrpPanel()
    End Sub

    '===============================
    '  子功能切换（服务方 / 日志输出 / 服务器面板）
    '===============================

    Private Sub TabFrp_Check(sender As Object, raiseByMouse As Boolean) Handles BtnFrpTabServer.Check, BtnFrpTabLog.Check, BtnFrpTabPanel.Check
        PanFrpServer.Visibility = If(BtnFrpTabServer.Checked, Visibility.Visible, Visibility.Collapsed)
        PanFrpLog.Visibility = If(BtnFrpTabLog.Checked, Visibility.Visible, Visibility.Collapsed)
        PanFrpPanel.Visibility = If(BtnFrpTabPanel.Checked, Visibility.Visible, Visibility.Collapsed)
    End Sub

    '===============================
    '  选择配置来源（两个点击框）
    '===============================

    Private Sub CardFrpIni_Click() Handles CardFrpIni.MouseLeftButtonUp
        FrpMode = "ini"
        CardFrpSelectedIni.Visibility = Visibility.Visible
        CardFrpSelectedPaste.Visibility = Visibility.Collapsed
        LogFrp("已选择模式：导入 ini 配置文件。")
    End Sub
    Private Sub CardFrpPaste_Click() Handles CardFrpPaste.MouseLeftButtonUp
        FrpMode = "paste"
        CardFrpSelectedIni.Visibility = Visibility.Collapsed
        CardFrpSelectedPaste.Visibility = Visibility.Visible
        LogFrp("已选择模式：手动粘贴 txt 代码。")
    End Sub

    Private Sub BtnFrpChooseFile_Click() Handles BtnFrpChooseFile.Click
        Dim Dlg = New OpenFileDialog() With {
            .Title = "选择 frpc 配置文件",
            .Filter = "配置文件|*.ini;*.toml;*.txt|所有文件|*.*"
        }
        If Dlg.ShowDialog() = True Then
            FrpConfigPath = Dlg.FileName
            LabFrpFilePath.Text = FrpConfigPath
            LogFrp("已选择配置文件：" & FrpConfigPath)
        End If
    End Sub

    '===============================
    '  下载 frpc
    '===============================

    Private Async Sub BtnFrpDownload_Click() Handles BtnFrpDownload.Click
        BtnFrpDownload.IsEnabled = False
        Try
            LogFrp("开始获取 frpc 下载地址……")
            Dim Url As String = Await GetFrpcUrlAsync()
            LogFrp("下载地址：" & Url)
            Directory.CreateDirectory(PathFrp)
            Dim Temp As String = PathFrp & "frp_dl.tmp"
            If File.Exists(Temp) Then File.Delete(Temp)
            Await DownloadFileAsync(Url, Temp, Sub(d, t) RunInUi(Sub() LogFrp($"下载中… {d \ 1024} / {t \ 1024} KB")))
            LogFrp("下载完成，正在解压……")
            If IsZip(Temp) Then
                ExtractZip(Temp, PathFrp)
                File.Delete(Temp)
                Dim Exe = FindFile(PathFrp, "frpc.exe")
                If Exe Is Nothing Then Throw New Exception("解压后未找到 frpc.exe，请手动到工具目录查看。")
                FrpExePath = Exe
                LogFrp("已就绪：" & Exe)
                Hint("frpc 下载并解压完成！", HintType.Green)
            Else
                FrpExePath = PathFrp & "frpc.exe"
                If File.Exists(FrpExePath) Then File.Delete(FrpExePath)
                File.Move(Temp, FrpExePath)
                LogFrp("已下载 frpc.exe。")
                Hint("frpc 下载完成！", HintType.Green)
            End If
        Catch ex As Exception
            LogFrp("下载失败：" & ex.Message)
            Hint("frpc 下载失败，请查看日志。", HintType.Red)
        Finally
            BtnFrpDownload.IsEnabled = True
            RunInUi(AddressOf RefreshFrpPanel)
        End Try
    End Sub

    '===============================
    '  启动 / 停止
    '===============================

    Private Sub BtnFrpStart_Click() Handles BtnFrpStart.Click
        If ProcFrpc IsNot Nothing AndAlso Not ProcFrpc.HasExited Then
            Hint("frpc 已在运行！", HintType.Blue) : Return
        End If
        If String.IsNullOrEmpty(FrpExePath) OrElse Not File.Exists(FrpExePath) Then
            Hint("请先在“服务方”中下载 frpc 客户端！", HintType.Red) : Return
        End If
        If String.IsNullOrEmpty(FrpMode) Then
            Hint("请先选择配置来源（导入 ini 或 粘贴 txt）！", HintType.Red) : Return
        End If
        Try
            Dim Config = GetFrpcConfigFile()
            LogFrp("使用配置：" & Config)
            LogFrp($"启动 frpc：{FrpExePath} -c ""{Config}""")
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
            AddHandler ProcFrpc.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then LogFrp(e.Data)
            AddHandler ProcFrpc.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then LogFrp("[ERR] " & e.Data)
            AddHandler ProcFrpc.Exited, Sub(s, e) RunInUi(Sub() RefreshFrpPanel())
            ProcFrpc.Start()
            ProcFrpc.BeginOutputReadLine()
            ProcFrpc.BeginErrorReadLine()
            LogFrp("frpc 已启动，正在建立映射……")
            Hint("frpc 已启动！", HintType.Green)
        Catch ex As Exception
            LogFrp("启动失败：" & ex.Message)
            Hint("frpc 启动失败：" & ex.Message, HintType.Red)
        End Try
        RunInUi(AddressOf RefreshFrpPanel)
    End Sub

    Private Sub BtnFrpStop_Click() Handles BtnFrpStop.Click, BtnFrpStop2.Click
        StopFrpc()
    End Sub
    Private Sub StopFrpc()
        Try
            If ProcFrpc IsNot Nothing AndAlso Not ProcFrpc.HasExited Then
                ProcFrpc.Kill()
                LogFrp("已停止 frpc。")
            Else
                LogFrp("frpc 并未运行。")
            End If
        Catch ex As Exception
            LogFrp("停止失败：" & ex.Message)
        Finally
            ProcFrpc = Nothing
            RunInUi(AddressOf RefreshFrpPanel)
        End Try
    End Sub

    ''' <summary>
    ''' 关闭启动时强制结束 frpc 进程。
    ''' </summary>
    Public Sub KillOnExit()
        Try
            If ProcFrpc IsNot Nothing AndAlso Not ProcFrpc.HasExited Then ProcFrpc.Kill()
        Catch
        End Try
        ProcFrpc = Nothing
    End Sub

    Private Sub BtnFrpOpenFolder_Click() Handles BtnFrpOpenFolder.Click
        Try
            Directory.CreateDirectory(PathFrp)
            Process.Start(New ProcessStartInfo(PathFrp) With {.UseShellExecute = True})
        Catch ex As Exception
            LogFrp("打开目录失败：" & ex.Message)
        End Try
    End Sub
    Private Sub BtnFrpLogClear_Click() Handles BtnFrpLogClear.Click
        TxtFrpLog.Clear()
    End Sub

    '===============================
    '  服务器面板
    '===============================

    Private Sub RefreshFrpPanel()
        If ProcFrpc IsNot Nothing AndAlso Not ProcFrpc.HasExited Then
            LabFrpStatus.Text = "映射中"
            LabFrpPid.Text = ProcFrpc.Id.ToString()
        Else
            LabFrpStatus.Text = "未运行"
            LabFrpPid.Text = "—"
        End If
        LabFrpConfig.Text = If(String.IsNullOrEmpty(FrpConfigPath), If(FrpMode = "paste", "粘贴文本（临时文件）", "—"), FrpConfigPath)
    End Sub

    '===============================
    '  配置 / 日志 辅助
    '===============================

    Private Function GetFrpcConfigFile() As String
        If FrpMode = "ini" Then
            If String.IsNullOrEmpty(FrpConfigPath) OrElse Not File.Exists(FrpConfigPath) Then
                Throw New Exception("请先选择 ini 配置文件。")
            End If
            Return FrpConfigPath
        Else
            Dim Text = TxtFrpPaste.Text
            If String.IsNullOrWhiteSpace(Text) Then Throw New Exception("请先在文本框中粘贴 frpc 配置。")
            Dim Ext = If(Text.Contains("[["), ".toml", ".ini")
            Dim F = PathFrp & "frpc_config" & Ext
            File.WriteAllText(F, Text, Encoding.UTF8)
            Return F
        End If
    End Function

    Private Sub LogFrp(line As String)
        RunInUi(Sub()
                    TxtFrpLog.AppendText(line & vbCrLf)
                    TxtFrpLog.ScrollToEnd()
                End Sub)
    End Sub

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

    Private Async Function GetFrpcUrlAsync() As Task(Of String)
        Dim Json = Await Client.GetStringAsync("https://api.github.com/repos/fatedier/frp/releases/latest")
        Dim M = Regex.Match(Json, """browser_download_url""\s*:\s*""(?<u>[^""]*windows_amd64[^""]*\.zip)""")
        If M.Success Then Return M.Groups("u").Value.Replace("\/", "/")
        Throw New Exception("未能获取 frpc 下载地址（windows_amd64）。")
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

    Private Function FindFile(Root As String, Name As String) As String
        For Each F In Directory.EnumerateFiles(Root, Name, SearchOption.AllDirectories)
            Return F
        Next
        Return Nothing
    End Function

End Class
