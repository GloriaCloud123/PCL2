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
    End Sub

    '===============================
    '  手风琴板块切换
    '===============================

    Private Sub HeadMsl_Click() Handles HeadMsl.MouseLeftButtonUp
        ToggleSection(BodyMsl, ArrowMsl, "Msl")
    End Sub

    Private Sub HeadFrp_Click() Handles HeadFrp.MouseLeftButtonUp
        ToggleSection(BodyFrp, ArrowFrp, "Frp")
    End Sub

    Private Sub HeadGlc_Click() Handles HeadGlc.MouseLeftButtonUp
        ToggleSection(BodyGlc, ArrowGlc, "Glc")
    End Sub

    Private Sub ToggleSection(Body As StackPanel, Arrow As Path, Kind As String)
        Dim Opening = Body.Visibility <> Visibility.Visible
        '手风琴：先收起全部
        CollapseSection(BodyMsl, ArrowMsl)
        CollapseSection(BodyFrp, ArrowFrp)
        CollapseSection(BodyGlc, ArrowGlc)
        If Opening Then
            Select Case Kind
                Case "Msl" : EnsureMslContent()
                Case "Frp" : EnsureFrpContent()
            End Select
            Body.Visibility = Visibility.Visible
            Body.Opacity = 0
            AniStart({
                AaOpacity(Body, 1, 180,, New AniEaseOutFluent),
                AaTranslateY(Body, -10, 180,, New AniEaseOutFluent)
            }, "OnlineHubBody " & Kind)
        End If
        '箭头旋转（展开 180°，收起 0°）
        AniStart({AaRotateTransform(Arrow, If(Opening, 180, 0), 160,, New AniEaseOutFluent)}, "OnlineHubArrow " & Arrow.Name)
    End Sub

    Private Sub CollapseSection(Body As StackPanel, Arrow As Path)
        If Body.Visibility = Visibility.Visible Then
            Body.Visibility = Visibility.Collapsed
        End If
        AniStart({AaRotateTransform(Arrow, 0, 160)}, "OnlineHubArrow " & Arrow.Name)
    End Sub

    '===============================
    '  嵌入 MSL / FRP 页面内容
    '===============================

    Private Sub EnsureMslContent()
        If ContentMsl.Content IsNot Nothing Then Return
        PageMsl = New PageMslMain()
        Dim C = TryCast(PageMsl.Content, FrameworkElement)
        If C Is Nothing Then Return
        PageMsl.Content = Nothing
        C.MinHeight = 620
        C.Margin = New Thickness(0)
        ContentMsl.Content = C
        PageMsl.EnsureInit()
    End Sub

    Private Sub EnsureFrpContent()
        If ContentFrp.Content IsNot Nothing Then Return
        PageFrp = New PageFrpMain()
        Dim C = TryCast(PageFrp.Content, FrameworkElement)
        If C Is Nothing Then Return
        PageFrp.Content = Nothing
        C.MinHeight = 620
        C.Margin = New Thickness(0)
        ContentFrp.Content = C
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
