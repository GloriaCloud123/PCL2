Imports System.Diagnostics
Imports System.Runtime.InteropServices

Public Class PageOtherTest

    '===== 内存清理：参考 Mem Reduct 方案 =====
    '进程级：精简工作集（psapi.EmptyWorkingSet），无需管理员
    <DllImport("psapi.dll")>
    Private Shared Function EmptyWorkingSet(hProcess As IntPtr) As Boolean
    End Function

    '系统级：清空系统内存列表（ntdll.NtSetSystemInformation，SystemMemoryListInformation = 0x50）
    <DllImport("ntdll.dll")>
    Private Shared Function NtSetSystemInformation(InfoClass As Integer, ByRef Info As SYSTEM_MEMORY_LIST_COMMAND, Length As Integer) As Integer
    End Function

    '启用特权（ntdll.RtlAdjustPrivilege），SE_PROFILE_SINGLE_PROCESS_PRIVILEGE = 13
    <DllImport("ntdll.dll")>
    Private Shared Function RtlAdjustPrivilege(Privilege As UInteger, Enable As Boolean, CurrentThread As Boolean, ByRef Enabled As Boolean) As Integer
    End Function

    <StructLayout(LayoutKind.Sequential)>
    Private Structure SYSTEM_MEMORY_LIST_COMMAND
        Public Command As UInteger
        Public Status As UInteger
    End Structure

    '内存列表命令（与 Mem Reduct 保持一致）
    Private Const MEMORY_EMPTY_WORKING_SETS As UInteger = 1
    Private Const MEMORY_FLUSH_MODIFIED_LIST As UInteger = 2
    Private Const MEMORY_PURGE_STANDBY_LIST As UInteger = 3
    Private Const MEMORY_PURGE_LOW_PRIORITY_STANDBY_LIST As UInteger = 4
    Private Const SYSTEM_MEMORY_LIST_INFORMATION As Integer = &H50

    Private Sub PageOtherTest_Loaded() Handles Me.Loaded
        RefreshMemoryInfo()
    End Sub

    Private Sub RefreshMemoryInfo()
        Try
            Dim RamTotal As Double = Math.Round(My.Computer.Info.TotalPhysicalMemory / 1024 / 1024 / 1024, 1)
            Dim RamAvail As Double = Math.Round(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024 / 1024, 1)
            Dim ProcMem As Double = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024, 1)
            TextMemoryInfo.Text = "系统内存：共 " & RamTotal & " GB，可用 " & RamAvail & " GB。" & vbCrLf & _
                                 "启动器占用内存：" & ProcMem & " MB。"
        Catch ex As Exception
        End Try
    End Sub

    '================ 内存清理 ================

    Private Sub BtnMemory_Click() Handles BtnMemory.Click
        MemoryOptimize(True)
    End Sub

    '系统级内存清理（Mem Reduct 核心）：清空工作集 / 修改页列表 / 备用列表
    '成功返回 True；缺少管理员权限时各命令会失败，由调用方优雅降级
    Private Shared Function SystemMemoryClean() As Boolean
        Dim commands() As UInteger = {
            MEMORY_EMPTY_WORKING_SETS,
            MEMORY_FLUSH_MODIFIED_LIST,
            MEMORY_PURGE_STANDBY_LIST,
            MEMORY_PURGE_LOW_PRIORITY_STANDBY_LIST
        }
        Dim anyOk As Boolean = False
        For Each cmdVal As UInteger In commands
            Dim cmd As SYSTEM_MEMORY_LIST_COMMAND
            cmd.Command = cmdVal
            cmd.Status = 0
            Dim status As Integer = NtSetSystemInformation(SYSTEM_MEMORY_LIST_INFORMATION, cmd, Marshal.SizeOf(Of SYSTEM_MEMORY_LIST_COMMAND))
            If status >= 0 Then anyOk = True
        Next
        Return anyOk
    End Function

    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        If ShowHint Then Hint("正在清理内存（Mem Reduct 方案）……", HintType.Blue)
        Dim before As Double = 0, after As Double = 0
        Try
            Try
                before = CDbl(My.Computer.Info.AvailablePhysicalMemory)
            Catch
            End Try

            '启用 SE_PROFILE_SINGLE_PROCESS_PRIVILEGE(13)，用于系统级内存列表清理
            Try
                Dim enabled As Boolean = False
                RtlAdjustPrivilege(13, True, False, enabled)
            Catch
            End Try

            '系统级清理（Mem Reduct 核心，需管理员权限；失败则仅做进程级）
            Dim sysOk As Boolean = False
            Try
                sysOk = SystemMemoryClean()
            Catch
            End Try

            '进程级：精简启动器自身工作集
            Try
                EmptyWorkingSet(Process.GetCurrentProcess().Handle)
            Catch
            End Try
            '进程级：精简正在运行的游戏 Java 进程工作集
            For Each JavaProc In Process.GetProcessesByName("java")
                Try
                    EmptyWorkingSet(JavaProc.Handle)
                    JavaProc.Dispose()
                Catch
                End Try
            Next

            '回收 .NET 托管内存
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()

            Try
                after = CDbl(My.Computer.Info.AvailablePhysicalMemory)
            Catch
            End Try

            Dim freedMB As Double = 0
            If before > 0 AndAlso after > 0 Then freedMB = Math.Round((after - before) / 1024 / 1024, 0)

            Dim msg As String = "内存清理完成！"
            If sysOk Then
                If freedMB > 0 Then msg &= " 系统可用内存释放约 " & freedMB & " MB。"
            Else
                msg &= "（系统级清理需管理员权限，已执行进程级清理）"
            End If
            If ShowHint Then Hint(msg, HintType.Green)
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
