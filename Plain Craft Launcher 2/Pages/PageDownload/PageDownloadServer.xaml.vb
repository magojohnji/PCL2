Public Class PageDownloadServer

    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanBack, Nothing, DlClientListLoader, AddressOf Load_OnFinish)
    End Sub
    Private Sub Init() Handles Me.Loaded
        PanBack.ScrollToHome()
    End Sub

    Private Sub Load_OnFinish()
        '结果数据化
        Try
            '归类
            Dim Dict As New Dictionary(Of String, List(Of JObject)) From {
                {"正式版", New List(Of JObject)}, {"预览版", New List(Of JObject)}, {"远古版", New List(Of JObject)}, {"愚人节版", New List(Of JObject)}
            }
            Dim Versions As JArray = DlClientListLoader.Output.Value("versions")
            For Each Version As JObject In Versions
                '确定分类
                Dim Type As String = Version("type")
                Select Case Type
                    Case "release"
                        Type = "正式版"
                    Case "snapshot"
                        Type = "预览版"
                        'Mojang 误分类
                        If Version("id").ToString.StartsWith("1.") AndAlso
                            Not Version("id").ToString.ToLower.Contains("combat") AndAlso
                            Not Version("id").ToString.ToLower.Contains("rc") AndAlso
                            Not Version("id").ToString.ToLower.Contains("experimental") AndAlso
                            Not Version("id").ToString.ToLower.Contains("pre") Then
                            Type = "正式版"
                            Version("type") = "release"
                        End If
                        '愚人节版本
                        Select Case Version("id").ToString.ToLower
                            Case "20w14infinite", "20w14∞"
                                Type = "愚人节版"
                                Version("id") = "20w14∞"
                                Version("type") = "special"
                                Version.Add("lore", GetMcFoolName(Version("id")))
                            Case "3d shareware v1.34", "1.rv-pre1", "15w14a", "2.0", "22w13oneblockatatime", "23w13a_or_b", "24w14potato"
                                Type = "愚人节版"
                                Version("type") = "special"
                                Version.Add("lore", GetMcFoolName(Version("id")))
                        End Select
                    Case "special"
                        '已被处理的愚人节版
                        Type = "愚人节版"
                    Case Else
                        Type = "远古版"
                End Select
                '加入辞典
                Dict(Type).Add(Version)
            Next
            '排序
            For i = 0 To Dict.Keys.Count - 1
                Dict(Dict.Keys(i)) = Sort(Dict.Values(i),
                                          Function(a, b) a("releaseTime").Value(Of Date) > b("releaseTime").Value(Of Date))
            Next
            '清空当前
            PanMain.Children.Clear()
            '添加最新版本
            Dim CardInfo As New MyCard With {.Title = "最新版本", .Margin = New Thickness(0, 0, 0, 15), .SwapType = 2}
            Dim TopestVersions As New List(Of JObject)
            Dim Release As JObject = Dict("正式版")(0).DeepClone()
            Release("lore") = "最新正式版，发布于 " & Release("releaseTime").Value(Of Date).ToString("yyyy'/'MM'/'dd HH':'mm")
            TopestVersions.Add(Release)
            If Dict("正式版")(0)("releaseTime").Value(Of Date) < Dict("预览版")(0)("releaseTime").Value(Of Date) Then
                Dim Snapshot As JObject = Dict("预览版")(0).DeepClone()
                Snapshot("lore") = "最新预览版，发布于 " & Snapshot("releaseTime").Value(Of Date).ToString("yyyy'/'MM'/'dd HH':'mm")
                TopestVersions.Add(Snapshot)
            End If
            Dim PanInfo As New StackPanel With {.Margin = New Thickness(20, MyCard.SwapedHeight, 18, 0), .VerticalAlignment = VerticalAlignment.Top, .RenderTransform = New TranslateTransform(0, 0), .Tag = TopestVersions}
            MyCard.StackInstall(PanInfo, 2)
            CardInfo.Children.Add(PanInfo)
            PanMain.Children.Add(CardInfo)
            '添加其他版本
            For Each Pair As KeyValuePair(Of String, List(Of JObject)) In Dict
                If Not Pair.Value.Any() Then Continue For
                '增加卡片
                Dim NewCard As New MyCard With {.Title = Pair.Key & " (" & Pair.Value.Count & ")", .Margin = New Thickness(0, 0, 0, 15), .SwapType = 2}
                Dim NewStack As New StackPanel With {.Margin = New Thickness(20, MyCard.SwapedHeight, 18, 0), .VerticalAlignment = VerticalAlignment.Top, .RenderTransform = New TranslateTransform(0, 0), .Tag = Pair.Value}
                NewCard.Children.Add(NewStack)
                NewCard.SwapControl = NewStack
                NewCard.IsSwaped = True
                PanMain.Children.Add(NewCard)
            Next
        Catch ex As Exception
            Log(ex, "可视化 MC 版本列表出错", LogLevel.Feedback)
        End Try
    End Sub

    Public Sub SaveServer_Click(sender As Object, e As EventArgs)
        Dim File As CompFile = If(TypeOf sender Is MyListItem, sender, sender.Parent).Tag
        RunInNewThread(
        Sub()
            Try
                Dim Desc As String = "服务端"
                '确认默认保存位置
                Dim DefaultFolder As String = Nothing
                RunInUi(
                    Sub()
                        '弹窗要求选择保存位置
                        Dim Target As String
                        Target = SelectAs("选择保存位置", "FileName", Desc & "文件|" & "*.jar", DefaultFolder)
                        If Not Target.Contains("\") Then Exit Sub
                        '构造步骤加载器
                        Dim LoaderName As String = Desc & "下载：" & GetFileNameWithoutExtentionFromPath(Target) & " "
                        Dim Loaders As New List(Of LoaderBase)
                        Loaders.Add(New LoaderDownload("下载文件", New List(Of NetFile) From {File.ToNetFile(Target)}) With {.ProgressWeight = 6, .Block = True})
                        '启动
                        Dim Loader As New LoaderCombo(Of Integer)(LoaderName, Loaders) With {.OnStateChanged = AddressOf DownloadStateSave}
                        Loader.Start(1)
                        LoaderTaskbarAdd(Loader)
                        FrmMain.BtnExtraDownload.ShowRefresh()
                        FrmMain.BtnExtraDownload.Ribble()
                    End Sub)
            Catch ex As Exception
                Log(ex, "保存原版服务端失败", LogLevel.Feedback)
            End Try
        End Sub, "Download Vanilla Server Save")
    End Sub

    Public Sub DownloadStart(sender As MyListItem, e As Object)
        McDownloadServer(NetPreDownloadBehaviour.HintWhileExists, sender.Title, sender.Tag("url").ToString)
    End Sub

    ''介绍栏
    'Private Sub BtnWeb_Click(sender As Object, e As EventArgs) Handles BtnWeb.Click
    '    OpenWebsite("https://www.minecraft.net/zh-hans")
    'End Sub
    'Private Sub BtnInstall_Click(sender As Object, e As EventArgs) Handles BtnInstall.Click
    '    FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall)
    'End Sub

End Class
