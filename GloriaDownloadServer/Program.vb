Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
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

    '' 上游映射（顺序重要：长前缀在前）
    Private ReadOnly UPSTREAM As New List(Of KeyValuePair(Of String, String)) From {
        New KeyValuePair(Of String, String)("piston-data/", "https://piston-data.mojang.com/"),
        New KeyValuePair(Of String, String)("piston-meta/", "https://piston-meta.mojang.com/"),
        New KeyValuePair(Of String, String)("libraries/", "https://libraries.minecraft.net/"),
        New KeyValuePair(Of String, String)("resources/", "https://resources.download.minecraft.net/"),
        New KeyValuePair(Of String, String)("launcher/", "https://launcher.mojang.com/"),
        New KeyValuePair(Of String, String)("launcher-meta/", "https://launchermeta.mojang.com/"),
        New KeyValuePair(Of String, String)("v1/products/", "https://piston-meta.mojang.com/v1/products/"),
        New KeyValuePair(Of String, String)("mc/", "https://launchermeta.mojang.com/mc/")
    }

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

        Console.WriteLine("============================================")
        Console.WriteLine("  GloriaCloud 下载源 & MC 原版下载站")
        Console.WriteLine("============================================")
        Console.WriteLine("  下载源（代理 Mojang 官方 CDN）: http://0.0.0.0:" & API_PORT)
        Console.WriteLine("  MC 原版下载站面板          : http://0.0.0.0:" & PANEL_PORT)
        Console.WriteLine("  关闭此窗口即可停止服务。")
        Console.WriteLine("")

        Try
            Task.WaitAll(ListenLoop(API_PORT, True), ListenLoop(PANEL_PORT, False))
        Catch ex As Exception
            Console.WriteLine("服务异常退出: " & ex.Message)
        End Try
    End Sub

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

    Private Async Function ListenLoop(port As Integer, isApi As Boolean) As Task
        Dim listener As New TcpListener(IPAddress.Any, port)
        listener.Start()
        Console.WriteLine("[" & (If(isApi, "下载源", "下载站面板")) & "] 已监听 :" & port)
        While True
            Dim client As TcpClient = Nothing
            Try
                client = Await listener.AcceptTcpClientAsync()
            Catch ex As Exception
                Console.WriteLine("接受连接失败: " & ex.Message)
                Exit While
            End Try
            HandleClient(client, isApi)
        End While
    End Function

    Private Async Function HandleClient(client As TcpClient, isApi As Boolean) As Task
        Try
            Using client
                client.ReceiveTimeout = 30000
                client.SendTimeout = 600000
                Dim ns As NetworkStream = client.GetStream()
                Dim headerBytes = Await ReadHeadersAsync(ns)
                If headerBytes Is Nothing Then Return
                Dim headerText As String = Encoding.ASCII.GetString(headerBytes)
                Dim lines() As String = headerText.Split(New String() {vbCrLf}, StringSplitOptions.None)
                If lines.Length = 0 Then Return
                Dim parts() As String = lines(0).Split(" "c)
                If parts.Length < 2 Then Return
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
                    Await ServeApi(ns, fullPath, headers)
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
    End Function

    '' ============ 端口 3888：下载源（反向代理 Mojang 官方 CDN） ============
    Private Async Function ServeApi(ns As NetworkStream, fullPath As String, headers As Dictionary(Of String, String)) As Task
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

        Dim upstream As String = UpstreamFor(pathPart)
        If upstream Is Nothing Then
            Await Send(ns, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"))
            Return
        End If
        If query <> "" Then upstream &= "?" & query

        Dim req As New HttpRequestMessage(HttpMethod.Get, upstream)
        req.Headers.TryAddWithoutValidation("User-Agent", "GloriaCloud-Source")
        If headers.ContainsKey("range") Then
            req.Headers.TryAddWithoutValidation("Range", headers("range"))
        End If
        Try
            Using resp As HttpResponseMessage = Await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
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
            End Using
        Catch ex As Exception
            Await Send(ns, 502, "Bad Gateway", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("上游错误: " & ex.Message))
        End Try
    End Function

    Private Function UpstreamFor(path As String) As String
        For Each kv As KeyValuePair(Of String, String) In UPSTREAM
            If path.StartsWith(kv.Key) Then
                Return kv.Value & path.Substring(kv.Key.Length)
            End If
        Next
        Return Nothing
    End Function

    '' ============ 端口 3889：MC 原版下载站面板 ============
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

End Module
