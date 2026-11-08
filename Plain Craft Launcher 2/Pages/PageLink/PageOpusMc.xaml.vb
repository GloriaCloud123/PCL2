Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Microsoft.Win32
Imports System.Diagnostics
Imports PCL

''' <summary>
''' OpusMC 网页版 Minecraft：内置 Offline 网页版。
''' 两种游玩方式：
'''   1) PCL 内直接游玩 —— 启动本地服务器并用内嵌 WebBrowser 加载（自动适配窗口）；
'''   2) 通过浏览器打开 —— 启动本地服务器（默认 80 端口，可更改），自动用浏览器（优先 Edge）打开。
''' </summary>
Public Class PageOpusMc

    '游戏资源目录与下载地址（opusmc_offline.zip 由本地下载源服务提供）
    Private Const PathOpu As String = "GloriaTools\OpusMc\"
    Private Const OpuZip As String = "opusmc_offline.zip"
    Private Const OpuUrl As String = "http://localhost:8765/" & OpuZip

    Private Shared ReadOnly Client As New HttpClient()

    '在类型首次使用前（早于内嵌 WebBrowser 控件创建）写入 IE 引擎兼容注册表，确保 WebBrowser 以 IE11 模式渲染
    Shared Sub New()
        SetWebBrowserEmulation()
    End Sub

    '实际提供静态文件的目录（含 index.html 的那一层）
    Private ServeRoot As String = ""

    '本地服务器（两个模式各一个）
    Private ListenerInPcl As HttpListener = Nothing
    Private ThreadInPcl As Thread = Nothing
    Private PortInPcl As Integer = 0
    Private ListenerBrowser As HttpListener = Nothing
    Private ThreadBrowser As Thread = Nothing
    Private PortBrowser As Integer = 0

    'MIME 映射
    Private Shared ReadOnly Mime As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {".html", "text/html"}, {".htm", "text/html"}, {".js", "application/javascript"},
        {".mjs", "application/javascript"}, {".css", "text/css"}, {".json", "application/json"},
        {".png", "image/png"}, {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".gif", "image/gif"},
        {".svg", "image/svg+xml"}, {".ico", "image/x-icon"}, {".webp", "image/webp"},
        {".wasm", "application/wasm"}, {".zip", "application/zip"}, {".mp3", "audio/mpeg"},
        {".ogg", "audio/ogg"}, {".wav", "audio/wav"}, {".mp4", "video/mp4"}, {".webm", "video/webm"},
        {".woff", "font/woff"}, {".woff2", "font/woff2"}, {".ttf", "font/ttf"},
        {".txt", "text/plain"}, {".pdf", "application/pdf"}
    }

    Private Sub Init() Handles Me.Loaded
        '默认展示「PCL 内直接游玩」
        SwitchTab("InPcl")
        RefreshStatus()
    End Sub

    Public Sub EnsureInit()
        '联机中心以内容形式嵌入时，手动触发初始化
        Init()
    End Sub

    '===============================
    '  板块切换
    '===============================

    Private Sub TabOpu_Check(sender As Object, raiseByMouse As Boolean) Handles BtnTabInPcl.Check, BtnTabBrowser.Check
        If sender Is BtnTabInPcl Then SwitchTab("InPcl") Else SwitchTab("Browser")
    End Sub

    Private Sub SwitchTab(Kind As String)
        PanInPcl.Visibility = If(Kind = "InPcl", Visibility.Visible, Visibility.Collapsed)
        PanBrowser.Visibility = If(Kind = "Browser", Visibility.Visible, Visibility.Collapsed)
        BtnTabInPcl.SetChecked(Kind = "InPcl", False, False)
        BtnTabBrowser.SetChecked(Kind = "Browser", False, False)
        '切换到浏览器板块时刷新资源提示
        If Kind = "Browser" Then RefreshStatus()
    End Sub

    '===============================
    '  游戏资源：下载 / 解压
    '===============================

    Private Function GameReady() As Boolean
        If ServeRoot = "" OrElse Not Directory.Exists(ServeRoot) Then Return False
        Return File.Exists(ServeRoot & "index.html")
    End Function

    Private Sub RefreshStatus()
        Dim Ready = GameReady()
        RunInUi(Sub()
                    If Ready Then
                        LabInPclStatus.Text = "游戏资源已就绪"
                        LabInPclStatus.Foreground = New SolidColorBrush(Colors.Green)
                        LabOpuRes.Text = "游戏资源已就绪（位于 " & ServeRoot & "）。"
                        BtnOpuStart.IsEnabled = True
                        BtnOpuOpenBrowser.IsEnabled = True
                    Else
                        LabInPclStatus.Text = "尚未下载游戏资源"
                        LabInPclStatus.Foreground = New SolidColorBrush(Colors.Gray)
                        LabOpuRes.Text = "尚未下载游戏资源，请先到「PCL 内直接游玩」点击「下载 / 更新资源」。"
                        BtnOpuStart.IsEnabled = False
                        BtnOpuOpenBrowser.IsEnabled = False
                    End If
                End Sub)
    End Sub

    Private Sub BtnOpuDl_Click() Handles BtnOpuDl.Click
        BtnOpuDl.IsEnabled = False
        RunInNewThread(Sub()
                           Try
                               DlShow("正在下载 OpusMC 离线包……", Nothing)
                               Dim Root = Paths.AppDataThenName & PathOpu
                               Directory.CreateDirectory(Root)
                               Dim Zip = Root & OpuZip
                               DownloadFile(OpuUrl, Zip, "正在下载 OpusMC 离线包")
                               DlShow("正在解压 OpusMC 离线包……", Nothing)
                               '清空旧内容后解压（仅清理本启动器自己的目录）
                               DeleteContents(Root, Zip)
                               System.IO.Compression.ZipFile.ExtractToDirectory(Zip, Root)
                               ServeRoot = FindServeRoot(Root)
                               DlHide()
                               RefreshStatus()
                               Hint("游戏资源已就绪，可以开始游玩！", HintType.Green)
                           Catch ex As Exception
                               DlHide()
                               Hint("下载或解压失败：" & ex.Message & "。请确认下载源服务（localhost:8765）已启动。", HintType.Red)
                           Finally
                               RunInUi(Sub() BtnOpuDl.IsEnabled = True)
                           End Try
                       End Sub, "OpuDownload")
    End Sub

    '递归查找 index.html 所在目录作为静态服务根
    Private Function FindServeRoot(Root As String) As String
        Dim Hit = FindFile(Root, "index.html")
        If Hit IsNot Nothing Then Return Path.GetDirectoryName(Hit) & "\"
        Return Root
    End Function

    Private Function FindFile(Root As String, Name As String) As String
        If Not Directory.Exists(Root) Then Return Nothing
        For Each F In Directory.EnumerateFiles(Root, Name, SearchOption.AllDirectories)
            Return F
        Next
        Return Nothing
    End Function

    Private Sub DeleteContents(Root As String, KeepFile As String)
        Try
            For Each F In Directory.GetFiles(Root)
                If F.Equals(KeepFile, StringComparison.OrdinalIgnoreCase) Then Continue For
                Try File.Delete(F) Catch : End Try
            Next
            For Each D In Directory.GetDirectories(Root)
                Try Directory.Delete(D, True) Catch : End Try
            Next
        Catch : End Try
    End Sub

    '===============================
    '  板块一：PCL 内直接游玩
    '===============================

    Private Sub BtnOpuStart_Click() Handles BtnOpuStart.Click
        If Not GameReady() Then Hint("请先下载游戏资源！", HintType.Red) : Return
        If ListenerInPcl IsNot Nothing AndAlso ListenerInPcl.IsListening Then
            Hint("游戏已在 PCL 内运行中。", HintType.Blue) : Return
        End If
        PortInPcl = StartStaticServer(ServeRoot, 25566, ListenerInPcl, ThreadInPcl)
        If PortInPcl < 0 Then
            Hint("本地服务器启动失败，请检查端口是否被占用。", HintType.Red) : Return
        End If
        PanInPclEmpty.Visibility = Visibility.Collapsed
        BrowserInPcl.Navigate("http://127.0.0.1:" & PortInPcl & "/")
        Hint("已在 PCL 内启动游戏，窗口已自动适配。", HintType.Green)
    End Sub

    Private Sub BtnOpuStop_Click() Handles BtnOpuStop.Click
        StopServer(ListenerInPcl)
        ListenerInPcl = Nothing : ThreadInPcl = Nothing : PortInPcl = 0
        PanInPclEmpty.Visibility = Visibility.Visible
        Try BrowserInPcl.Navigate("about:blank") Catch : End Try
        Hint("已停止 PCL 内游戏。", HintType.Blue)
    End Sub

    '===============================
    '  板块二：通过浏览器打开
    '===============================

    Private Sub BtnOpuOpenBrowser_Click() Handles BtnOpuOpenBrowser.Click
        If Not GameReady() Then Hint("请先下载游戏资源！", HintType.Red) : Return
        Dim Port = ParsePort()
        If Port <= 0 Then Hint("请输入有效的端口号（1-65535）。", HintType.Red) : Return
        '若已在运行，先停掉再用新端口重启
        If ListenerBrowser IsNot Nothing AndAlso ListenerBrowser.IsListening Then
            StopServer(ListenerBrowser) : ListenerBrowser = Nothing : ThreadBrowser = Nothing
        End If
        PortBrowser = StartStaticServer(ServeRoot, Port, ListenerBrowser, ThreadBrowser)
        If PortBrowser < 0 Then
            RunInUi(Sub()
                        LabOpuServer.Text = "启动失败：端口 " & Port & " 被占用或无权限。" &
                            If(Port < 1024, "（1024 以下端口通常需要以管理员身份运行，建议改用 8080。）", "（请更换端口后重试。）")
                    End Sub)
            Hint("本地服务器启动失败，请更换端口或右键以管理员身份运行。", HintType.Red) : Return
        End If
        Dim Url = "http://127.0.0.1:" & PortBrowser & "/"
        OpenBrowser(Url)
        RunInUi(Sub() LabOpuServer.Text = "服务器运行中：" & Url & vbCrLf & "（若浏览器未自动弹出，请手动在浏览器打开该地址）")
        Hint("已通过浏览器打开游戏页面。", HintType.Green)
    End Sub

    Private Sub BtnOpuStopServer_Click() Handles BtnOpuStopServer.Click
        StopServer(ListenerBrowser)
        ListenerBrowser = Nothing : ThreadBrowser = Nothing : PortBrowser = 0
        RunInUi(Sub() LabOpuServer.Text = "服务器已停止。")
        Hint("已停止本地服务器。", HintType.Blue)
    End Sub

    '解析端口下拉：默认 80，可手动输入自定义端口
    Private Function ParsePort() As Integer
        Dim T = (ComboOpuPort.Text & "").Trim()
        If T = "" OrElse T.Contains("默认") Then Return 80
        Dim N As Integer
        If Integer.TryParse(T, N) AndAlso N >= 1 AndAlso N <= 65535 Then Return N
        Return -1
    End Function

    '自动检测并打开浏览器：优先 Edge（microsoft-edge: 协议），否则系统默认浏览器
    Private Sub OpenBrowser(Url As String)
        Try
            Process.Start("microsoft-edge:" & Url)
            Return
        Catch
        End Try
        Try
            Process.Start(Url)
        Catch ex As Exception
            Hint("无法打开浏览器：" & ex.Message & "，请手动访问 " & Url, HintType.Red)
        End Try
    End Sub

    '===============================
    '  本地静态文件服务器（HttpListener）
    '===============================

    Private Function StartStaticServer(Root As String, Preferred As Integer, ByRef Listener As HttpListener, ByRef Th As Thread) As Integer
        Dim Candidates As New List(Of Integer)()
        If Preferred = 80 Then
            Candidates.AddRange({80, 8080, 25565})
        Else
            Candidates.AddRange({Preferred, Preferred + 1, Preferred + 2})
        End If
        For Each p In Candidates
            Try
                Dim L = New HttpListener()
                L.Prefixes.Add("http://127.0.0.1:" & p & "/")
                L.Start()
                Listener = L
                Th = RunInNewThread(Sub() ServeLoop(L, Root), "OpusServer:" & p)
                Return p
            Catch ex As HttpListenerException
                '端口被占用或无权限（1024 以下需管理员）：尝试下一个候选端口
            Catch ex As Exception
                Return -1
            End Try
        Next
        Return -1
    End Function

    Private Sub ServeLoop(Listener As HttpListener, Root As String)
        Try
            While Listener.IsListening
                Dim Ctx = Listener.GetContext()
                Try
                    HandleContext(Ctx, Root)
                Catch
                End Try
            End While
        Catch
        End Try
    End Sub

    Private Sub HandleContext(Ctx As HttpListenerContext, Root As String)
        Dim Resp = Ctx.Response
        Try
            Dim UrlPath = Ctx.Request.Url.AbsolutePath
            If UrlPath = "/" OrElse UrlPath = "" Then UrlPath = "/index.html"
            Dim Rel = UrlPath.TrimStart("/"c).Replace("/"c, "\"c)
            Dim Full = Path.GetFullPath(Path.Combine(Root, Rel))
            Dim RootFull = Path.GetFullPath(Root)
            If Not Full.StartsWith(RootFull, StringComparison.OrdinalIgnoreCase) Then
                Resp.StatusCode = 403 : Resp.Close() : Return
            End If
            If Directory.Exists(Full) Then Full = Path.Combine(Full, "index.html")
            If Not File.Exists(Full) Then
                Resp.StatusCode = 404
                Dim B = Encoding.UTF8.GetBytes("404 Not Found")
                Resp.OutputStream.Write(B, 0, B.Length)
                Resp.Close() : Return
            End If
            Dim Bytes = File.ReadAllBytes(Full)
            Dim Ext = Path.GetExtension(Full)
            Resp.ContentType = If(Mime.ContainsKey(Ext), Mime(Ext), "application/octet-stream")
            Resp.ContentLength64 = Bytes.Length
            Resp.OutputStream.Write(Bytes, 0, Bytes.Length)
            Resp.Close()
        Catch
            Try : Resp.StatusCode = 500 : Resp.Close() : Catch : End Try
        End Try
    End Sub

    Private Sub StopServer(ByRef L As HttpListener)
        If L IsNot Nothing Then
            Try L.Stop() Catch : End Try
            Try L.Close() Catch : End Try
        End If
        L = Nothing
    End Sub

    '===============================
    '  浏览器引擎兼容（IE11 模式）
    '===============================

    Private Shared Sub SetWebBrowserEmulation()
        Try
            Dim Exe = Process.GetCurrentProcess().MainModule.ModuleName
            Using Key = Registry.CurrentUser.CreateSubKey("Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION")
                Key.SetValue(Exe, 11001, RegistryValueKind.DWord)
            End Using
        Catch
        End Try
    End Sub

    '===============================
    '  下载辅助（含实时进度）
    '===============================

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

    Private Sub DlHide()
        RunInUi(Sub() If PanDlCard IsNot Nothing Then PanDlCard.Visibility = Visibility.Collapsed)
    End Sub

    Private Sub DownloadFile(Url As String, Dest As String, Optional Label As String = "")
        Directory.CreateDirectory(Path.GetDirectoryName(Dest))
        Using Resp = Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
            Resp.EnsureSuccessStatusCode()
            Dim Total = Resp.Content.Headers.ContentLength
            If Label <> "" Then
                If Total.HasValue AndAlso Total.Value > 0 Then DlShow(Label, 0.0) Else DlShow(Label, Nothing)
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

End Class
