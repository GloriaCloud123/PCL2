Imports System
Imports System.Collections.Generic
Imports System.IO
Imports Microsoft.VisualBasic
Imports System.Net
Imports System.Net.Sockets
Imports System.Net.NetworkInformation
Imports System.Net.Http
Imports System.Reflection
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

Module Program

    Private Const API_PORT As Integer = 3888
    Private Const PANEL_PORT As Integer = 3889

    Private httpClient As HttpClient
    Private logoBytes() As Byte
    Private home3888 As String
    Private panelHtml As String

    ' 上游映射（顺序重要：长前缀在前）。每个前缀对应多个上游候选：Mojang 官方优先，BMCLAPI 兜底
    Private ReadOnly MOJANG_UPSTREAM_TIMEOUT_SEC As Integer = 8
    Private ReadOnly MIRROR_UPSTREAM_TIMEOUT_SEC As Integer = 20
    Private ReadOnly UPSTREAM As New List(Of KeyValuePair(Of String, List(Of String))) From {
        New KeyValuePair(Of String, List(Of String))("v1/products/", New List(Of String) From {"https://piston-meta.mojang.com/v1/products", "https://bmclapi2.bangbang93.com/v1/products"}),
        New KeyValuePair(Of String, List(Of String))("launcher-meta/", New List(Of String) From {"https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"}),
        New KeyValuePair(Of String, List(Of String))("piston-meta/", New List(Of String) From {"https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"}),
        New KeyValuePair(Of String, List(Of String))("piston-data/", New List(Of String) From {"https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"}),
        New KeyValuePair(Of String, List(Of String))("libraries/", New List(Of String) From {"https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven"}),
        New KeyValuePair(Of String, List(Of String))("resources/", New List(Of String) From {"https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets"}),
        New KeyValuePair(Of String, List(Of String))("launcher/", New List(Of String) From {"https://launcher.mojang.com"}),
        New KeyValuePair(Of String, List(Of String))("mc/", New List(Of String) From {"https://launchermeta.mojang.com/mc", "https://bmclapi2.bangbang93.com/mc"})
    }

    ' UPnP 状态
    Private upnpControlUrl As String = ""
    Private upnpServiceType As String = ""

    Sub Main()
        Try
            logoBytes = LoadBytes("GloriaDownloadServer.Resources.logo.png")
            home3888 = LoadText("GloriaDownloadServer.Resources.home3888.html")
            panelHtml = LoadText("GloriaDownloadServer.Resources.panel.html")
        Catch ex As Exception
            Console.WriteLine("加载内嵌资源失败: " & ex.Message)
        End Try

        Dim handler As New HttpClientHandler() With {.AutomaticDecompression = DecompressionMethods.None}
        httpClient = New HttpClient(handler)
        httpClient.Timeout = TimeSpan.FromMinutes(10)

        Console.WriteLine("====================================================")
        Console.WriteLine("  GloriaCloud 下载源 + MC 原版下载站 + 端口映射")
        Console.WriteLine("====================================================")
        Console.WriteLine("  内部服务（仅本机回环）: 127.0.0.1:" & API_PORT & " / :" & PANEL_PORT)
        Console.WriteLine("  关闭此窗口即可停止全部服务。")
        Console.WriteLine("")

        Dim lanIP As String = GetLanIP()
        Dim serverTasks As New List(Of Task)()

        If lanIP <> "" Then
            ' 内部服务只绑定回环，由端口映射器对外暴露
            serverTasks.Add(StartServer(IPAddress.Loopback, API_PORT, True))
            serverTasks.Add(StartServer(IPAddress.Loopback, PANEL_PORT, False))
            ' 端口映射：把 局域网IP:端口 转发到 127.0.0.1:端口（同 WiFi / 路由器设备可访问）
            serverTasks.Add(StartForwarder(lanIP, API_PORT, API_PORT))
            serverTasks.Add(StartForwarder(lanIP, PANEL_PORT, PANEL_PORT))
            Console.WriteLine("  局域网访问地址（同路由器下的手机 / 电脑）:")
            Console.WriteLine("    下载源 : http://" & lanIP & ":" & API_PORT)
            Console.WriteLine("    下载站 : http://" & lanIP & ":" & PANEL_PORT)
        Else
            ' 未检测到局域网地址，直接全网监听（仍仅本机可用）
            serverTasks.Add(StartServer(IPAddress.Any, API_PORT, True))
            serverTasks.Add(StartServer(IPAddress.Any, PANEL_PORT, False))
            Console.WriteLine("  未检测到局域网地址，仅本机回环可用。")
        End If

        Console.WriteLine("")
        Console.WriteLine("  本机访问：")
        Console.WriteLine("    下载源 : http://127.0.0.1:" & API_PORT)
        Console.WriteLine("    下载站 : http://127.0.0.1:" & PANEL_PORT)
        Console.WriteLine("")
        Console.WriteLine("  正在尝试 UPnP 公网端口映射（需路由器支持，失败则仅局域网可用）……")

        ' UPnP 异步执行，不阻塞主服务
        Dim upnpTask As Task = Task.Run(Sub() TryUPnP(lanIP))

        Try
            Task.WaitAll(serverTasks.ToArray())
        Catch ex As Exception
            Console.WriteLine("服务异常退出: " & ex.Message)
        End Try
    End Sub

    ' ============ 资源加载 ============
    Private Function LoadBytes(name As String) As Byte()
        Using s As Stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            If s Is Nothing Then Return New Byte() {}
            Dim ms As New MemoryStream()
            s.CopyTo(ms)
            Return ms.ToArray()
        End Using
    End Function

    Private Function LoadText(name As String) As String
        Return Encoding.UTF8.GetString(LoadBytes(name))
    End Function

    ' ============ 局域网 IP 获取 ============
    Private Function GetLanIP() As String
        Try
            For Each ni As NetworkInterface In NetworkInterface.GetAllNetworkInterfaces()
                If ni.OperationalStatus = OperationalStatus.Up Then
                    Dim typ As NetworkInterfaceType = ni.NetworkInterfaceType
                    If typ = NetworkInterfaceType.Ethernet OrElse typ = NetworkInterfaceType.Wireless80211 OrElse typ = NetworkInterfaceType.GigabitEthernet Then
                        For Each ua As UnicastIPAddressInformation In ni.GetIPProperties().UnicastAddresses
                            If ua.Address.AddressFamily = AddressFamily.InterNetwork Then
                                Dim ip As String = ua.Address.ToString()
                                If Not ip.StartsWith("169.254") AndAlso Not ip.StartsWith("127.") Then Return ip
                            End If
                        Next
                    End If
                End If
            Next
        Catch
        End Try
        Return ""
    End Function

    ' ============ 内部 HTTP 服务（绑定指定地址） ============
    Private Function StartServer(bindAddr As IPAddress, port As Integer, isApi As Boolean) As Task
        Return Task.Run(Async Function()
            Dim name As String = If(isApi, "下载源", "下载站面板")
            Dim listener As New TcpListener(bindAddr, port)
            Dim started As Boolean = False
            While Not started
                Try
                    listener.Start()
                    started = True
                Catch ex As Exception
                    Console.WriteLine("[" & name & "] 端口 " & port & " 启动失败（可能已被占用）：" & ex.Message & "  3 秒后重试……")
                    Thread.Sleep(3000)
                End Try
            End While
            Console.WriteLine("[" & name & "] 内部监听 " & bindAddr.ToString() & ":" & port)
            While True
                Dim client As TcpClient = Nothing
                Try
                    client = Await listener.AcceptTcpClientAsync()
                Catch ex As Exception
                    Console.WriteLine("[" & name & "] 接受连接失败: " & ex.Message)
                    Exit While
                End Try
                HandleClient(client, isApi)
            End While
        End Function)
    End Function

    ' ============ 端口映射转发器：外部IP:外端口 -> 127.0.0.1:内端口 ============
    Private Function StartForwarder(bindIP As String, externalPort As Integer, internalPort As Integer) As Task
        Return Task.Run(Async Function()
            Dim listener As New TcpListener(IPAddress.Parse(bindIP), externalPort)
            Dim started As Boolean = False
            While Not started
                Try
                    listener.Start()
                    started = True
                Catch ex As Exception
                    Console.WriteLine("[端口映射] " & bindIP & ":" & externalPort & " 启动失败（端口可能被占用）：" & ex.Message & "  3 秒后重试……")
                    Thread.Sleep(3000)
                End Try
            End While
            Console.WriteLine("[端口映射] " & bindIP & ":" & externalPort & " -> 127.0.0.1:" & internalPort & "（已开放给局域网）")
            While True
                Dim client As TcpClient = Nothing
                Try
                    client = Await listener.AcceptTcpClientAsync()
                Catch ex As Exception
                    Console.WriteLine("[端口映射] 接受连接失败: " & ex.Message)
                    Exit While
                End Try
                ForwardClient(client, internalPort)
            End While
        End Function)
    End Function

    Private Function ForwardClient(client As TcpClient, internalPort As Integer) As Task
        Return Task.Run(Async Function()
            Try
                Using client
                    Dim target As New TcpClient()
                    Await target.ConnectAsync(IPAddress.Loopback, internalPort)
                    Using target
                        Dim cs As NetworkStream = client.GetStream()
                        Dim ts As NetworkStream = target.GetStream()
                        Dim t1 As Task = Pump(cs, ts)
                        Dim t2 As Task = Pump(ts, cs)
                        Await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(3600000))
                    End Using
                End Using
            Catch
            End Try
        End Function)
    End Function

    Private Async Function Pump(fromStream As NetworkStream, toStream As NetworkStream) As Task
        Dim buf(32767) As Byte
        Dim n As Integer
        Try
            While (n = Await fromStream.ReadAsync(buf, 0, buf.Length)) > 0
                Await toStream.WriteAsync(buf, 0, n)
            End While
        Catch
        End Try
    End Function

    ' ============ HTTP 客户端处理 ============
    Private Async Function HandleClient(client As TcpClient, isApi As Boolean) As Task
        Try
            Using client
                client.ReceiveTimeout = 30000
                client.SendTimeout = 600000
                Dim ns As NetworkStream = client.GetStream()
                Dim headerBytes As Byte() = Await ReadHeadersAsync(ns)
                If headerBytes Is Nothing Then Return
                Dim headerText As String = Encoding.ASCII.GetString(headerBytes)
                Dim lines() As String = headerText.Split(New String() {vbCrLf}, StringSplitOptions.None)
                If lines.Length = 0 Then Return
                Dim parts() As String = lines(0).Split(" "c)
                If parts.Length < 2 Then Return
                Dim reqMethod As String = parts(0).ToUpper()
                Dim fullPath As String = parts(1)
                Dim headers As New Dictionary(Of String, String)()
                For i As Integer = 1 To lines.Length - 1
                    Dim line As String = lines(i)
                    If line = "" Then Continue For
                    Dim ci As Integer = line.IndexOf(":"c)
                    If ci > 0 Then
                        Dim key As String = line.Substring(0, ci).Trim().ToLower()
                        If Not headers.ContainsKey(key) Then headers(key) = line.Substring(ci + 1).Trim()
                    End If
                Next
                If isApi Then
                    Await ServeApi(ns, reqMethod, fullPath, headers)
                Else
                    Await ServePanel(ns, fullPath, headers)
                End If
            End Using
        Catch ex As Exception
        End Try
    End Function

    Private Async Function ReadHeadersAsync(ns As NetworkStream) As Task(Of Byte())
        Dim buf As New MemoryStream()
        Dim tmp(2047) As Byte
        While True
            Dim n As Integer = Await ns.ReadAsync(tmp, 0, tmp.Length)
            If n <= 0 Then Return Nothing
            buf.Write(tmp, 0, n)
            Dim arr() As Byte = buf.ToArray()
            For i As Integer = 0 To arr.Length - 4
                If arr(i) = 13 AndAlso arr(i + 1) = 10 AndAlso arr(i + 2) = 13 AndAlso arr(i + 3) = 10 Then
                    Return arr
                End If
            Next
            If buf.Length > 65536 Then Return Nothing
        End While
        Return Nothing
    End Function

    ' ============ 端口 3888：下载源（反向代理，Mojang 优先 + BMCLAPI 自动兜底） ============
    Private Async Function ServeApi(ns As NetworkStream, method As String, fullPath As String, headers As Dictionary(Of String, String)) As Task
        Dim qi As Integer = fullPath.IndexOf("?"c)
        Dim pathPart As String = If(qi >= 0, fullPath.Substring(0, qi), fullPath)
        Dim query As String = If(qi >= 0, fullPath.Substring(qi + 1), "")

        If pathPart = "/" OrElse pathPart = "" Then
            Await Send(ns, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(home3888))
            Return
        End If
        If pathPart = "/logo.png" Then
            Await Send(ns, 200, "OK", "image/png", logoBytes)
            Return
        End If
        If pathPart = "/favicon.ico" Then
            Await Send(ns, 204, "No Content", "", Nothing)
            Return
        End If

        Dim match As KeyValuePair(Of String, List(Of String)) = UpstreamFor(pathPart)
        If match.Value Is Nothing Then
            Console.WriteLine("[下载源] " & Now.ToString("HH:mm:ss") & " 404 未映射路径: " & pathPart)
            Await Send(ns, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"))
            Return
        End If

        Dim isHead As Boolean = (method = "HEAD")
        Dim errMsg As String = Nothing
        For Each baseUrl As String In match.Value
            Dim upstream As String = baseUrl & pathPart.Substring(match.Key.Length)
            If query <> "" Then upstream &= "?" & query
            Dim timeoutSec As Integer = If(upstream.Contains("mojang"), MOJANG_UPSTREAM_TIMEOUT_SEC, MIRROR_UPSTREAM_TIMEOUT_SEC)
            Dim cts As New CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
            Try
                Dim req As New HttpRequestMessage(HttpMethod.Get, upstream)
                req.Headers.TryAddWithoutValidation("User-Agent", "GloriaCloud-Source")
                If headers.ContainsKey("range") Then
                    req.Headers.TryAddWithoutValidation("Range", headers("range"))
                End If
                Dim resp As HttpResponseMessage = Await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                cts.CancelAfter(Timeout.InfiniteTimeSpan) '取消仅用于响应头阶段，正文传输不再受超时影响
                Using resp
                Dim statusCode As Integer = CInt(resp.StatusCode)
                Dim ct As String = If(resp.Content.Headers.ContentType IsNot Nothing, resp.Content.Headers.ContentType.ToString(), "application/octet-stream")
                Dim cl As Long? = resp.Content.Headers.ContentLength
                Dim sb As New StringBuilder()
                sb.Append("HTTP/1.1 " & statusCode & " " & resp.ReasonPhrase & vbCrLf)
                sb.Append("Connection: close" & vbCrLf)
                sb.Append("Access-Control-Allow-Origin: *" & vbCrLf)
                sb.Append("Content-Type: " & ct & vbCrLf)
                sb.Append("Accept-Ranges: bytes" & vbCrLf)
                If cl.HasValue Then
                    sb.Append("Content-Length: " & cl.Value & vbCrLf)
                Else
                    sb.Append("Transfer-Encoding: chunked" & vbCrLf)
                End If
                sb.Append(vbCrLf)
                Dim headerArr() As Byte = Encoding.ASCII.GetBytes(sb.ToString())
                Await ns.WriteAsync(headerArr, 0, headerArr.Length)
                Console.WriteLine("[下载源] " & Now.ToString("HH:mm:ss") & " " & method & " " & pathPart & " -> " & statusCode & " (" & upstream & ")")
                If Not isHead Then
                    Using stream As Stream = Await resp.Content.ReadAsStreamAsync()
                        Dim buf(65535) As Byte
                        Dim read As Integer
                        If cl.HasValue Then
                            While (read = Await stream.ReadAsync(buf, 0, buf.Length)) > 0
                                Await ns.WriteAsync(buf, 0, read)
                            End While
                        Else
                            While (read = Await stream.ReadAsync(buf, 0, buf.Length)) > 0
                                Dim chunkHead() As Byte = Encoding.ASCII.GetBytes(read.ToString("x") & vbCrLf)
                                Await ns.WriteAsync(chunkHead, 0, chunkHead.Length)
                                Await ns.WriteAsync(buf, 0, read)
                                Await ns.WriteAsync(Encoding.ASCII.GetBytes(vbCrLf), 0, 2)
                            End While
                            Await ns.WriteAsync(Encoding.ASCII.GetBytes("0" & vbCrLf & vbCrLf), 0, 5)
                        End If
                    End Using
                End If
                End Using
                Return '成功，结束
            Catch ex As Exception
                Console.WriteLine("[下载源] " & Now.ToString("HH:mm:ss") & " 上游失败 (" & upstream & ") : " & ex.Message)
                errMsg = ex.Message
            Finally
                cts.Dispose()
            End Try
        Next
        Await Send(ns, 502, "Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("所有上游均失败，最后错误: " & errMsg & vbCrLf & "请检查本机网络，或稍后重试。"))
    End Function

    Private Function UpstreamFor(path As String) As KeyValuePair(Of String, List(Of String))
        For Each kv As KeyValuePair(Of String, List(Of String)) In UPSTREAM
            If path.StartsWith(kv.Key) Then
                Return kv
            End If
        Next
        Return New KeyValuePair(Of String, List(Of String))("", Nothing)
    End Function

    ' ============ 端口 3889：MC 原版下载站面板 ============
    Private Async Function ServePanel(ns As NetworkStream, fullPath As String, headers As Dictionary(Of String, String)) As Task
        If fullPath.StartsWith("/favicon.ico") Then
            Await Send(ns, 204, "No Content", "", Nothing)
            Return
        End If
        Dim hostVal As String = If(headers.ContainsKey("host"), headers("host"), "127.0.0.1")
        Dim apiHost As String
        If hostVal.Contains(":") AndAlso Not hostVal.StartsWith("[") Then
            apiHost = hostVal.Split(":"c)(0) & ":3888"
        Else
            apiHost = hostVal & ":3888"
        End If
        Dim html As String = panelHtml.Replace("__APIHOST__", apiHost).Replace("__APIPROTO__", "http").Replace("__PORT__", "3889")
        Await Send(ns, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html))
    End Function

    Private Async Function Send(ns As NetworkStream, status As Integer, reason As String, contentType As String, body As Byte()) As Task
        Dim sb As New StringBuilder()
        sb.Append("HTTP/1.1 " & status & " " & reason & vbCrLf)
        sb.Append("Connection: close" & vbCrLf)
        If contentType <> "" Then sb.Append("Content-Type: " & contentType & vbCrLf)
        If body IsNot Nothing Then sb.Append("Content-Length: " & body.Length & vbCrLf)
        sb.Append(vbCrLf)
        Dim headerArr() As Byte = Encoding.ASCII.GetBytes(sb.ToString())
        Await ns.WriteAsync(headerArr, 0, headerArr.Length)
        If body IsNot Nothing AndAlso body.Length > 0 Then Await ns.WriteAsync(body, 0, body.Length)
    End Function

    ' ============ UPnP 公网端口映射 ============
    Private Sub TryUPnP(lanIP As String)
        If lanIP = "" Then
            Console.WriteLine("  [UPnP] 无局域网地址，跳过公网映射。")
            Return
        End If
        Try
            Dim ctrl As String = UPnPDiscover()
            If ctrl = "" Then
                Console.WriteLine("  [UPnP] 未发现支持 UPnP 的路由器，公网映射不可用（仅局域网可访问）。")
                Return
            End If
            upnpControlUrl = ctrl
            UPnPAddPort(API_PORT, API_PORT, lanIP, "GloriaCloud-Download")
            UPnPAddPort(PANEL_PORT, PANEL_PORT, lanIP, "GloriaCloud-Panel")
            Dim ext As String = UPnPGetExternalIP()
            If ext <> "" Then
                Console.WriteLine("  [UPnP] 公网映射成功！外部访问地址：")
                Console.WriteLine("    下载源 : http://" & ext & ":" & API_PORT)
                Console.WriteLine("    下载站 : http://" & ext & ":" & PANEL_PORT)
                Console.WriteLine("    （若此地址仍为内网 / CGNAT 地址，则外网仍无法访问，请咨询运营商）")
            Else
                Console.WriteLine("  [UPnP] 已添加端口映射，但无法获取公网 IP。")
            End If
        Catch ex As Exception
            Console.WriteLine("  [UPnP] 映射失败（" & ex.Message & "），仅局域网可访问。")
        End Try
    End Sub

    Private Function UPnPDiscover() As String
        Try
            Using udp As New UdpClient()
                Dim msg As String = "M-SEARCH * HTTP/1.1" & vbCrLf & _
                    "HOST: 239.255.255.250:1900" & vbCrLf & _
                    "MAN: ""ssdp:discover""" & vbCrLf & _
                    "MX: 3" & vbCrLf & _
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1" & vbCrLf & vbCrLf
                Dim data() As Byte = Encoding.ASCII.GetBytes(msg)
                udp.Send(data, data.Length, "239.255.255.250", 1900)
                udp.Client.ReceiveTimeout = 3000
                Dim ep As New IPEndPoint(IPAddress.Any, 0)
                Dim start As DateTime = DateTime.Now
                While DateTime.Now.Subtract(start).TotalSeconds < 4
                    Try
                        Dim resp() As Byte = udp.Receive(ep)
                        Dim text As String = Encoding.UTF8.GetString(resp)
                        Dim loc As String = GetHeaderValue(text, "LOCATION")
                        If loc <> "" Then
                            Dim ctrl As String = UPnPGetControlURL(loc)
                            If ctrl <> "" Then Return ctrl
                        End If
                    Catch
                        Exit While
                    End Try
                End While
            End Using
        Catch
        End Try
        Return ""
    End Function

    Private Function GetHeaderValue(text As String, name As String) As String
        Dim lines() As String = text.Split(New String() {vbCrLf}, StringSplitOptions.None)
        For Each ln As String In lines
            If ln.StartsWith(name & ":", StringComparison.OrdinalIgnoreCase) Then
                Return ln.Substring(name.Length + 1).Trim()
            End If
        Next
        Return ""
    End Function

    Private Function UPnPGetControlURL(location As String) As String
        Try
            Dim xml As String = HttpGet(location, 5000)
            If xml = "" Then Return ""
            Dim types() As String = {"WANIPConnection:1", "WANPPPConnection:1"}
            For Each t As String In types
                Dim idx As Integer = xml.IndexOf(t, StringComparison.OrdinalIgnoreCase)
                If idx >= 0 Then
                    Dim stStart As Integer = xml.LastIndexOf("<serviceType>", idx, StringComparison.OrdinalIgnoreCase)
                    If stStart >= 0 Then
                        Dim stEnd As Integer = xml.IndexOf("</serviceType>", stStart, StringComparison.OrdinalIgnoreCase)
                        If stEnd > stStart Then
                            upnpServiceType = xml.Substring(stStart + "<serviceType>".Length, stEnd - (stStart + "<serviceType>".Length)).Trim()
                        End If
                    End If
                    Dim cidx As Integer = xml.IndexOf("<controlURL>", idx, StringComparison.OrdinalIgnoreCase)
                    If cidx >= 0 Then
                        Dim sidx As Integer = cidx + "<controlURL>".Length
                        Dim eidx As Integer = xml.IndexOf("</controlURL>", sidx, StringComparison.OrdinalIgnoreCase)
                        If eidx > sidx Then
                            Dim rel As String = xml.Substring(sidx, eidx - sidx).Trim()
                            Dim uri As New Uri(New Uri(location), rel)
                            Return uri.ToString()
                        End If
                    End If
                End If
            Next
        Catch
        End Try
        Return ""
    End Function

    Private Function HttpGet(url As String, timeoutMs As Integer) As String
        Try
            Dim req As HttpWebRequest = CType(WebRequest.Create(url), HttpWebRequest)
            req.Method = "GET"
            req.Timeout = timeoutMs
            req.KeepAlive = False
            Using resp As WebResponse = req.GetResponse()
                Using sr As New StreamReader(resp.GetResponseStream(), Encoding.UTF8)
                    Return sr.ReadToEnd()
                End Using
            End Using
        Catch
        End Try
        Return ""
    End Function

    Private Sub UPnPAddPort(externalPort As Integer, internalPort As Integer, internalClient As String, desc As String)
        If upnpControlUrl = "" OrElse upnpServiceType = "" Then Return
        Try
            Try
                UPnPSoap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" & externalPort & "</NewExternalPort><NewProtocol>TCP</NewProtocol>")
            Catch
            End Try
            Dim p As String = "<NewRemoteHost></NewRemoteHost>" & _
                "<NewExternalPort>" & externalPort & "</NewExternalPort>" & _
                "<NewProtocol>TCP</NewProtocol>" & _
                "<NewInternalPort>" & internalPort & "</NewInternalPort>" & _
                "<NewInternalClient>" & internalClient & "</NewInternalClient>" & _
                "<NewEnabled>1</NewEnabled>" & _
                "<NewPortMappingDescription>" & desc & "</NewPortMappingDescription>" & _
                "<NewLeaseDuration>0</NewLeaseDuration>"
            UPnPSoap("AddPortMapping", p)
        Catch ex As Exception
            Throw
        End Try
    End Sub

    Private Function UPnPGetExternalIP() As String
        If upnpControlUrl = "" OrElse upnpServiceType = "" Then Return ""
        Try
            Dim r As String = UPnPSoap("GetExternalIPAddress", "")
            Dim i As Integer = r.IndexOf("<NewExternalIPAddress>", StringComparison.OrdinalIgnoreCase)
            If i >= 0 Then
                Dim s As Integer = i + "<NewExternalIPAddress>".Length
                Dim e As Integer = r.IndexOf("</NewExternalIPAddress>", s, StringComparison.OrdinalIgnoreCase)
                If e > s Then Return r.Substring(s, e - s).Trim()
            End If
        Catch
        End Try
        Return ""
    End Function

    Private Function UPnPSoap(action As String, params As String) As String
        Dim serviceType As String = upnpServiceType
        Dim body As String = "<?xml version=""1.0""?>" & _
            "<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">" & _
            "<s:Body><u:" & action & " xmlns:u=""" & serviceType & """>" & params & "</u:" & action & "></s:Body></s:Envelope>"
        Dim req As HttpWebRequest = CType(WebRequest.Create(upnpControlUrl), HttpWebRequest)
        req.Method = "POST"
        req.ContentType = "text/xml; charset=""utf-8"""
        req.Headers.Add("SOAPACTION", """" & serviceType & "#" & action & """")
        req.Timeout = 8000
        Dim bytes() As Byte = Encoding.UTF8.GetBytes(body)
        req.ContentLength = bytes.Length
        Using s As Stream = req.GetRequestStream()
            s.Write(bytes, 0, bytes.Length)
        End Using
        Using resp As WebResponse = req.GetResponse()
            Using sr As New StreamReader(resp.GetResponseStream(), Encoding.UTF8)
                Return sr.ReadToEnd()
            End Using
        End Using
    End Function

End Module
