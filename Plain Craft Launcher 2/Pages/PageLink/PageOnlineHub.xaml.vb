Imports System.Windows.Controls
Imports PCL

''' <summary>
''' 联机中心：三种联机模式（MSL / FRP / GloriaCloud 服务器）以手风琴板块集中呈现。
''' MSL 与 FRP 页面内容在首次展开时动态嵌入；GloriaCloud 板块复用 PageLinkMain 的房间流程。
''' </summary>
Public Class PageOnlineHub

    '保持被嵌入页面的实例存活（事件处理器挂在页面实例上）
    Private PageMsl As PageMslMain = Nothing
    Private PageFrp As PageFrpMain = Nothing

    Private Sub Init() Handles Me.Loaded
        LoadGlcOptions()
        '默认展开 MSL 板块（互斥单开，保证 MSL 占满内容区高度、内部可滚动完整显示）
        EnsureMslContent()
        OpenSection(SecMsl, ArrowMsl, "Msl")
    End Sub

    '===============================
    '  手风琴板块切换（互斥单开，一次只展开一个板块）
    '===============================

    Private Sub HeadMsl_Click() Handles HeadMsl.MouseLeftButtonUp
        ToggleSection(SecMsl, ArrowMsl, "Msl")
    End Sub

    Private Sub HeadFrp_Click() Handles HeadFrp.MouseLeftButtonUp
        ToggleSection(SecFrp, ArrowFrp, "Frp")
    End Sub

    Private Sub HeadGlc_Click() Handles HeadGlc.MouseLeftButtonUp
        ToggleSection(SecGlc, ArrowGlc, "Glc")
    End Sub

    '展开某个板块并收起其余板块，确保当前板块占满内容区高度（内部 ScrollViewer 可正常工作）
    Private Sub OpenSection(Sec As Grid, Arrow As System.Windows.Shapes.Path, Kind As String)
        SecMsl.Visibility = If(Sec Is SecMsl, Visibility.Visible, Visibility.Collapsed)
        SecFrp.Visibility = If(Sec Is SecFrp, Visibility.Visible, Visibility.Collapsed)
        SecGlc.Visibility = If(Sec Is SecGlc, Visibility.Visible, Visibility.Collapsed)
        AniStart({AaRotateTransform(ArrowMsl, If(Sec Is SecMsl, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowMsl")
        AniStart({AaRotateTransform(ArrowFrp, If(Sec Is SecFrp, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowFrp")
        AniStart({AaRotateTransform(ArrowGlc, If(Sec Is SecGlc, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowGlc")
        If Sec.Visibility = Visibility.Visible Then
            Sec.Opacity = 0
            AniStart({AaOpacity(Sec, 1, 180,, New AniEaseOutFluent)}, "OnlineHubSec " & Kind)
        End If
    End Sub

    Private Sub ToggleSection(Sec As Grid, Arrow As System.Windows.Shapes.Path, Kind As String)
        If Sec.Visibility = Visibility.Visible Then
            '已展开则收起当前板块
            Sec.Visibility = Visibility.Collapsed
            AniStart({AaRotateTransform(Arrow, 0, 160,, New AniEaseOutFluent)}, "OnlineHubArrow " & Arrow.Name)
        Else
            '未展开则展开（互斥单开，自动收起其余板块）
            Select Case Kind
                Case "Msl" : EnsureMslContent()
                Case "Frp" : EnsureFrpContent()
            End Select
            OpenSection(Sec, Arrow, Kind)
        End If
    End Sub

    '===============================
    '  嵌入 MSL / FRP 页面内容
    '===============================

    Private Sub EnsureMslContent()
        If ContentMsl.Content IsNot Nothing Then Return
        '整页嵌入：直接把 PageMslMain 放入内容区，撑满高度以启用内部滚动，全部子页与控件完整呈现
        PageMsl = New PageMslMain()
        ContentMsl.Content = PageMsl
        PageMsl.EnsureInit()
    End Sub

    Private Sub EnsureFrpContent()
        If ContentFrp.Content IsNot Nothing Then Return
        '整页嵌入：直接把 PageFrpMain 放入内容区，frpc 内置功能完整呈现
        PageFrp = New PageFrpMain()
        ContentFrp.Content = PageFrp
        PageFrp.EnsureInit()
    End Sub

    '===============================
    '  GloriaCloud 服务器
    '===============================

    Private Sub LoadGlcOptions()
        ComboGlcAccel.SelectedIndex = If(Settings.Get(Of Integer)("LinkLatencyMode") = 1, 0, 1)
        TxtGlcPeer.Text = Settings.Get(Of String)("LinkCustomPeer")
    End Sub

    Private Sub BtnGlcCreate_Click() Handles BtnGlcCreate.Click
        SaveGlcOptions()
        FormMain.GoOnline("create")
    End Sub

    Private Sub BtnGlcJoin_Click() Handles BtnGlcJoin.Click
        Dim Code As String = TxtGlcCode.Text.Trim()
        If Code = "" Then
            Hint("请先输入房间码！", HintType.Red) : Return
        End If
        SaveGlcOptions()
        FormMain.GoOnline("join", Code)
    End Sub

    Private Sub TxtGlcCode_KeyDown(sender As Object, e As KeyEventArgs) Handles TxtGlcCode.KeyDown
        If e.Key = Key.Enter Then BtnGlcJoin_Click()
    End Sub

    Private Sub BtnGlcSave_Click() Handles BtnGlcSave.Click
        SaveGlcOptions()
        Hint("联机选项已保存，创建与加入双方需保持一致。", HintType.Green)
    End Sub

    Private Sub SaveGlcOptions()
        Settings.Set("LinkLatencyMode", If(ComboGlcAccel.SelectedIndex = 0, 1, 0))
        Settings.Set("LinkCustomPeer", TxtGlcPeer.Text.Trim())
    End Sub

End Class
