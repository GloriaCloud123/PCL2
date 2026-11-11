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
    Private PageOpu As PageOpusMc = Nothing

    Private Sub Init() Handles Me.Loaded
        LoadGlcOptions()
        '四个板块标题始终可见；默认不自动展开，让用户一眼看到全部四种联机方式，点哪个展开哪个
        EnsureMslContent()
    End Sub

    '===============================
    '  手风琴板块切换（互斥单开，一次只展开一个板块）
    '===============================

    Private Sub HeadMsl_Click() Handles HeadMsl.MouseLeftButtonUp
        ToggleSection(ContentMsl, ArrowMsl, "Msl")
    End Sub

    Private Sub HeadFrp_Click() Handles HeadFrp.MouseLeftButtonUp
        ToggleSection(ContentFrp, ArrowFrp, "Frp")
    End Sub

    Private Sub HeadGlc_Click() Handles HeadGlc.MouseLeftButtonUp
        ToggleSection(BodyGlc, ArrowGlc, "Glc")
    End Sub

    Private Sub HeadOpu_Click() Handles HeadOpu.MouseLeftButtonUp
        ToggleSection(ContentOpu, ArrowOpu, "Opu")
    End Sub

    '展开某个板块的 body 并收起其余板块的 body；标题区始终可见，当前 body 占满内容区高度（内部 ScrollViewer 可正常工作）
    Private Sub OpenSection(Body As FrameworkElement, Arrow As System.Windows.Shapes.Path, Kind As String)
        ContentMsl.Visibility = If(Body Is ContentMsl, Visibility.Visible, Visibility.Collapsed)
        ContentFrp.Visibility = If(Body Is ContentFrp, Visibility.Visible, Visibility.Collapsed)
        BodyGlc.Visibility = If(Body Is BodyGlc, Visibility.Visible, Visibility.Collapsed)
        ContentOpu.Visibility = If(Body Is ContentOpu, Visibility.Visible, Visibility.Collapsed)
        AniStart({AaRotateTransform(ArrowMsl, If(Body Is ContentMsl, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowMsl")
        AniStart({AaRotateTransform(ArrowFrp, If(Body Is ContentFrp, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowFrp")
        AniStart({AaRotateTransform(ArrowGlc, If(Body Is BodyGlc, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowGlc")
        AniStart({AaRotateTransform(ArrowOpu, If(Body Is ContentOpu, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow ArrowOpu")
        If Body.Visibility = Visibility.Visible Then
            Body.Opacity = 0
            AniStart({AaOpacity(Body, 1, 180,, New AniEaseOutFluent)}, "OnlineHubBody " & Kind)
        End If
    End Sub

    Private Sub ToggleSection(Body As FrameworkElement, Arrow As System.Windows.Shapes.Path, Kind As String)
        If Body.Visibility = Visibility.Visible Then
            '已展开则收起当前板块（body 折叠，标题保留可见）
            Body.Visibility = Visibility.Collapsed
            AniStart({AaRotateTransform(Arrow, 0, 160,, New AniEaseOutFluent)}, "OnlineHubArrow " & Arrow.Name)
        Else
            '未展开则展开（互斥单开，自动收起其余板块）
            Select Case Kind
                Case "Msl" : EnsureMslContent()
                Case "Frp" : EnsureFrpContent()
                Case "Opu" : EnsureOpuContent()
            End Select
            OpenSection(Body, Arrow, Kind)
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

    Private Sub EnsureOpuContent()
        If ContentOpu.Content IsNot Nothing Then Return
        '整页嵌入：直接把 PageOpusMc 放入内容区，撑满高度以启用内部滚动与自适应窗口
        PageOpu = New PageOpusMc()
        ContentOpu.Content = PageOpu
        PageOpu.EnsureInit()
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
