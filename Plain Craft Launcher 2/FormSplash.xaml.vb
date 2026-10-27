Imports System.Windows
Imports System.Windows.Threading

''' <summary>
''' 矢量启动画面：以矢量 Path 渲染 Logo，任意分辨率下不模糊；关闭时渐隐。
''' </summary>
Public Class FormSplash

    Private ClosingTimer As DispatcherTimer = Nothing

    Public Sub New()
        InitializeComponent()
        '居中显示
        WindowStartupLocation = WindowStartupLocation.Manual
        Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left
        Top = (SystemParameters.WorkArea.Height - Height) / 2 + SystemParameters.WorkArea.Top
        '淡入
        Opacity = 0
        BeginAnimation(OpacityProperty, New Windows.Media.Animation.DoubleAnimation(0, 1, New Duration(TimeSpan.FromMilliseconds(220))))
    End Sub

    ''' <summary>
    ''' 关闭启动画面（与 WPF 自带 SplashScreen.Close 保持一致的调用方式），先渐隐再关闭。
    ''' </summary>
    Public Sub Close(CloseDuration As TimeSpan)
        If Not IsLoaded Then Return
        Dim anim As New Windows.Media.Animation.DoubleAnimation(Opacity, 0, New Duration(If(CloseDuration = TimeSpan.Zero, TimeSpan.FromMilliseconds(120), CloseDuration)))
        AddHandler anim.Completed, Sub() MyBase.Close()
        BeginAnimation(OpacityProperty, anim)
    End Sub

End Class
