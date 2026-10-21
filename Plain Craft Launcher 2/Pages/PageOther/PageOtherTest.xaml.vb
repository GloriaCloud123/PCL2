Imports System.Diagnostics

Public Class PageOtherTest

    '释放进程工作集（物理内存）
    Private Declare Function SetProcessWorkingSetSize Lib "kernel32" (hProcess As IntPtr, dwMinimumWorkingSetSize As IntPtr, dwMaximumWorkingSetSize As IntPtr) As Boolean

    Private Sub PageOtherTest_Loaded() Handles Me.Loaded
        RefreshMemoryInfo()
    End Sub

    Private Sub RefreshMemoryInfo()
        Try
            Dim RamTotal As Double = Math.Round(My.Computer.Info.TotalPhysicalMemory / 1024 / 1024 / 1024, 1)
            Dim RamAvail As Double = Math.Round(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024 / 1024, 1)
            Dim ProcMem As Double = Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024, 1)
            TextMemoryInfo.Text = "系统内存：共 " & RamTotal & " GB，可用 " & RamAvail & " GB。" & vbCrLf & _
                                 "启动器占用内存：" & ProcMem & " MB。"
        Catch ex As Exception
        End Try
    End Sub

    '================ 内存清理 ================

    Private Sub BtnMemory_Click() Handles BtnMemory.Click
        MemoryOptimize(True)
    End Sub

    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        If ShowHint Then Hint("正在清理内存……", HintType.Blue)
        Try
            '回收 .NET 托管内存
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
            '释放物理工作集：启动器自身 + 游戏 Java 进程
            Try
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, New IntPtr(-1), New IntPtr(-1))
            Catch ex As Exception
            End Try
            For Each JavaProc In Process.GetProcessesByName("java")
                Try
                    SetProcessWorkingSetSize(JavaProc.Handle, New IntPtr(-1), New IntPtr(-1))
                    JavaProc.Dispose()
                Catch ex As Exception
                End Try
            Next
            If ShowHint Then Hint("内存清理完成！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "内存清理失败")
            If ShowHint Then Hint("内存清理失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Public Shared Sub MemoryOptimizeInternal(ShowHint As Boolean)
        MemoryOptimize(ShowHint)
    End Sub

    '================ 下载自定义文件 ================

    Private Sub BtnCustomDownload_Click() Handles BtnCustomDownload.Click
        Dim Url As String = InputCustomUrl.Text.Trim()
        If Url = "" Then
            Hint("请先填写文件下载链接。", HintType.Red)
            Return
        End If
        If Not (Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Then
            Url = "http://" & Url
        End If
        Dim Name As String = InputCustomName.Text.Trim()
        Dim Folder As String = InputCustomFolder.Text.Trim()
        StartCustomDownload(Url, If(Name = "", Nothing, Name), If(Folder = "", Nothing, Folder))
    End Sub

    Private Sub BtnCustomBrowse_Click() Handles BtnCustomBrowse.Click
        Dim Dialog As New System.Windows.Forms.FolderBrowserDialog() With {.Description = "选择下载保存文件夹"}
        If Dialog.ShowDialog() = System.Windows.Forms.DialogResult.OK Then
            InputCustomFolder.Text = Dialog.SelectedPath
        End If
    End Sub

    Public Shared Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        '确定保存目录与文件名
        Dim TargetFolder As String = If(Folder, PathTemp & "CustomDownload\")
        Try
            Directory.CreateDirectory(TargetFolder)
        Catch ex As Exception
        End Try
        If FileName Is Nothing OrElse FileName = "" Then FileName = PathUtils.GetLastPart(Url.Split("?"c)(0))
        If FileName = "" Then FileName = "未知文件"
        Dim LocalPath As String = TargetFolder.TrimEnd("\") & "\" & Path.GetFileName(FileName)
        RunInThread(Sub()
                        Try
                            Hint("正在下载自定义文件：" & Path.GetFileName(LocalPath) & " ……", HintType.Blue)
                            NetDownloadByClient(Url, LocalPath)
                            RunInUi(Sub()
                                        If ModMain.FrmOtherTest IsNot Nothing Then
                                            ModMain.FrmOtherTest.TextCustomStatus.Text = "下载完成：" & LocalPath
                                            ModMain.FrmOtherTest.RefreshMemoryInfo()
                                        End If
                                    End Sub)
                            Hint("自定义文件下载完成！", HintType.Green)
                            OpenExplorer(TargetFolder)
                        Catch ex As Exception
                            Logger.Error(ex, "自定义文件下载失败")
                            RunInUi(Sub()
                                        If ModMain.FrmOtherTest IsNot Nothing Then
                                            ModMain.FrmOtherTest.TextCustomStatus.Text = "下载失败：" & ex.Message
                                        End If
                                    End Sub)
                            Hint("自定义文件下载失败：" & ex.Message, HintType.Red)
                        End Try
                    End Sub)
    End Sub

    Public Shared Sub RubbishClear()
        Try
            If Directory.Exists(PathTemp & "Cache\") Then Directory.Delete(PathTemp & "Cache\", True)
            Hint("缓存清理完成！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "缓存清理失败")
            Hint("缓存清理失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Public Shared Sub Jrrp()
        Hint("GloriaCloud 百宝箱：今天也要元气满满地玩游戏哦！", HintType.Green)
    End Sub

    Public Shared Function GetRandomCave() As String
        Return "GloriaCloud 百宝箱已就位：内存清理 + 自定义下载，随取随用。"
    End Function

    Public Shared Function GetRandomHint() As String
        Return GetRandomCave()
    End Function

    Public Shared Function GetRandomPresetHint() As String
        Return GetRandomCave()
    End Function

End Class
