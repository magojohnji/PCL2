﻿Imports System.IO.Compression

Public Module ModModpack

#Region "安装"
    '触发整合包安装的外部接口
    ''' <summary>
    ''' 弹窗要求选择一个整合包文件并进行安装。
    ''' </summary>
    Public Sub ModpackInstall()
        Dim File As String = SelectFile("整合包文件(*.rar;*.zip;*.mrpack)|*.rar;*.zip;*.mrpack", "选择整合包压缩文件") '选择整合包文件
        If String.IsNullOrEmpty(File) Then Exit Sub
        RunInThread(Sub() ModpackInstall(File))
    End Sub
    ''' <summary>
    ''' 安装一个给定的整合包文件，返回启动的安装加载器，如果未成功启动则返回 Nothing。
    ''' 必须在工作线程执行。
    ''' </summary>
    Public Function ModpackInstall(File As String, Optional VersionName As String = Nothing, Optional ShowHint As Boolean = True, Optional Logo As String = Nothing) As LoaderCombo(Of String)
        Log("[ModPack] 整合包安装请求：" & If(File, "null"))
        Dim Archive As ZipArchive = Nothing
        Dim ArchiveBaseFolder As String = ""
        Try
            '获取整合包种类与关键 Json
            Dim PackType As Integer = -1
            Try
                Archive = New ZipArchive(New FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                '从根目录判断整合包类型
                If Archive.GetEntry("mcbbs.packmeta") IsNot Nothing Then PackType = 3 : Exit Try 'MCBBS 整合包（优先于 manifest.json 判断）
                If Archive.GetEntry("mmc-pack.json") IsNot Nothing Then PackType = 2 : Exit Try 'MMC 整合包（优先于 manifest.json 判断，#4194）
                If Archive.GetEntry("modrinth.index.json") IsNot Nothing Then PackType = 4 : Exit Try 'Modrinth 整合包
                If Archive.GetEntry("manifest.json") IsNot Nothing Then
                    Dim Json As JObject = GetJson(ReadFile(Archive.GetEntry("manifest.json").Open, Encoding.UTF8))
                    If Json("addons") Is Nothing Then
                        PackType = 0 : Exit Try 'CurseForge 整合包
                    Else
                        PackType = 3 : Exit Try 'MCBBS 整合包
                    End If
                End If
                If Archive.GetEntry("modpack.json") IsNot Nothing Then PackType = 1 : Exit Try 'HMCL 整合包
                '从一级目录判断整合包类型
                For Each Entry In Archive.Entries
                    Dim FullNames As String() = Entry.FullName.Split("/")
                    ArchiveBaseFolder = FullNames(0) & "/"
                    If Entry.FullName.EndsWithF("/versions/") AndAlso FullNames.Count = 3 Then PackType = 9 : Exit Try '压缩包
                    '确定为一级目录下
                    If FullNames.Count <> 2 Then Continue For
                    '判断是否为关键文件
                    If FullNames(1) = "mcbbs.packmeta" Then PackType = 3 : Exit Try 'MCBBS 整合包（优先于 manifest.json 判断）
                    If FullNames(1) = "mmc-pack.json" Then PackType = 2 : Exit Try 'MMC 整合包（优先于 manifest.json 判断，#4194）
                    If FullNames(1) = "modrinth.index.json" Then PackType = 4 : Exit Try 'Modrinth 整合包
                    If FullNames(1) = "manifest.json" Then
                        Dim Json As JObject = GetJson(ReadFile(Entry.Open, Encoding.UTF8))
                        If Json("addons") Is Nothing Then
                            PackType = 0 : Exit Try 'CurseForge 整合包
                        Else
                            PackType = 3 : ArchiveBaseFolder = "overrides/" : Exit Try 'MCBBS 整合包
                        End If
                    End If
                    If FullNames(1) = "modpack.json" Then PackType = 1 : Exit Try 'HMCL 整合包
                Next
            Catch ex As Exception
                If GetExceptionDetail(ex, True).Contains("Error.WinIOError") Then
                    Log(ex, "打开整合包文件失败", If(ShowHint, LogLevel.Hint, LogLevel.Normal))
                    Return Nothing
                ElseIf File.EndsWithF(".rar", True) Then
                    Log(ex, "PCL 无法处理 rar 格式的压缩包，请在解压后重新压缩为 zip 格式再试", If(ShowHint, LogLevel.Hint, LogLevel.Normal))
                    Return Nothing
                Else
                    Log(ex, "打开整合包文件失败，文件可能损坏或为不支持的压缩包格式", If(ShowHint, LogLevel.Hint, LogLevel.Normal))
                    Return Nothing
                End If
            End Try
            '执行对应的安装方法
            Select Case PackType
                Case 0
                    Log("[ModPack] 整合包种类：CurseForge")
                    Return InstallPackCurseForge(File, Archive, ArchiveBaseFolder, VersionName, Logo)
                Case 1
                    Log("[ModPack] 整合包种类：HMCL")
                    Return InstallPackHMCL(File, Archive, ArchiveBaseFolder)
                Case 2
                    Log("[ModPack] 整合包种类：MMC")
                    Return InstallPackMMC(File, Archive, ArchiveBaseFolder)
                Case 3
                    Log("[ModPack] 整合包种类：MCBBS")
                    Return InstallPackMCBBS(File, Archive, ArchiveBaseFolder, VersionName)
                Case 4
                    Log("[ModPack] 整合包种类：Modrinth")
                    Return InstallPackModrinth(File, Archive, ArchiveBaseFolder, VersionName, Logo)
                Case 9
                    Log("[ModPack] 整合包种类：压缩包")
                    Archive.Dispose()
                    Archive = Nothing
                    Return InstallPackCompress(File, ArchiveBaseFolder)
                Case Else
                    If ShowHint Then
                        Hint("未能识别该整合包的种类，无法安装！", HintType.Critical)
                    Else
                        Log("[ModPack] 未能识别该整合包的种类，无法安装！")
                    End If
                    Return Nothing
            End Select
        Catch ex As Exception
            Log(ex, "准备安装整合包失败", LogLevel.Feedback)
            Return Nothing
        Finally
            If Archive IsNot Nothing Then Archive.Dispose()
        End Try
    End Function

    '整合包缓存清理
    Private IsInstallCacheCleared As Boolean = False
    Private IsInstallCacheClearing As Boolean = False
    Private Sub UnpackFiles(InstallTemp As String, FileAddress As String, Loader As LoaderBase)
        '清理缓存文件夹
        If Not IsInstallCacheCleared Then
            IsInstallCacheCleared = True
            IsInstallCacheClearing = True
            Try
                Log("[ModPack] 开始清理整合包安装缓存")
                DeleteDirectory(PathTemp & "PackInstall\")
                Log("[ModPack] 已清理整合包安装缓存")
            Catch ex As Exception
                Log(ex, "清理整合包安装缓存失败")
            Finally
                IsInstallCacheClearing = False
            End Try
        ElseIf IsInstallCacheClearing Then
            '等待另一个整合包安装的清理步骤完成
            Do While IsInstallCacheClearing
                Thread.Sleep(1)
            Loop
        End If
        '解压文件
        Dim RetryCount As Integer = 1
        Dim Encode = Encoding.GetEncoding("GB18030")
        Try
Retry:
            '完全不知道为啥会出现文件正在被另一进程使用的问题，总之多试试
            DeleteDirectory(InstallTemp)
            ExtractFile(FileAddress, InstallTemp, Encode)
        Catch ex As Exception
            Log(ex, "第 " & RetryCount & " 次解压尝试失败")
            If TypeOf ex Is ArgumentException Then
                Encode = Encoding.UTF8
                Log("[ModPack] 已切换压缩包解压编码为 UTF8")
            End If
            If RetryCount < 5 Then
                Thread.Sleep(RetryCount * 2000)
                If Loader IsNot Nothing AndAlso Loader.LoadingState <> MyLoading.MyLoadingState.Run Then Exit Sub
                RetryCount += 1
                GoTo Retry
            Else
                Throw
            End If
        End Try
    End Sub

#Region "不同类型整合包的安装方法"

    'CurseForge
    Private Function InstallPackCurseForge(FileAddress As String, Archive As Compression.ZipArchive, ArchiveBaseFolder As String,
                                           Optional VersionName As String = Nothing, Optional Logo As String = Nothing) As LoaderCombo(Of String)

        '读取 Json 文件
        Dim Json As JObject
        Try
            Json = GetJson(ReadFile(Archive.GetEntry(ArchiveBaseFolder & "manifest.json").Open))
            If Json("minecraft") Is Nothing OrElse Json("minecraft")("version") Is Nothing Then Throw New Exception("整合包未提供 Minecraft 版本信息")
        Catch ex As Exception
            Log(ex, "CurseForge 整合包安装信息存在问题", LogLevel.Hint)
            Return Nothing
        End Try

        '获取版本名
        Dim ShowRibble As Boolean = VersionName Is Nothing
        If VersionName Is Nothing Then
            VersionName = If(Json("name"), "")
            Dim Validate As New ValidateFolderName(PathMcFolder & "versions")
            If Validate.Validate(VersionName) <> "" Then VersionName = ""
            If VersionName = "" Then VersionName = MyMsgBoxInput("输入版本名称", "", "", New ObjectModel.Collection(Of Validate) From {Validate})
            If String.IsNullOrEmpty(VersionName) Then Return Nothing
        End If

        '获取 Mod API 版本信息
        Dim ForgeVersion As String = Nothing
        Dim NeoForgeVersion As String = Nothing
        Dim FabricVersion As String = Nothing
        Dim QuiltVersion As String = Nothing
        For Each Entry In If(Json("minecraft")("modLoaders"), {})
            Dim Id As String = If(Entry("id"), "").ToString.ToLower
            If Id.StartsWithF("forge-") Then
                'Forge 指定
                If Id.Contains("recommended") Then
                    Log("[ModPack] 该整合包版本过老，已不支持进行安装！", LogLevel.Hint)
                    Return Nothing
                End If
                Try
                    Log("[ModPack] 整合包 Forge 版本：" & Id)
                    ForgeVersion = Id.Replace("forge-", "")
                    Exit For
                Catch ex As Exception
                    Log(ex, "读取整合包 Forge 版本失败：" & Id)
                End Try
            ElseIf Id.StartsWithF("neoforge-") Then
                'NeoForge 指定
                Try
                    Log("[ModPack] 整合包 NeoForge 版本：" & Id)
                    NeoForgeVersion = Id.Replace("neoforge-", "")
                    Exit For
                Catch ex As Exception
                    Log(ex, "读取整合包 NeoForge 版本失败：" & Id)
                End Try
            ElseIf Id.StartsWithF("fabric-") Then
                'Fabric 指定
                Try
                    Log("[ModPack] 整合包 Fabric 版本：" & Id)
                    FabricVersion = Id.Replace("fabric-", "")
                    Exit For
                Catch ex As Exception
                    Log(ex, "读取整合包 Fabric 版本失败：" & Id)
                End Try
            ElseIf Id.StartsWithF("quilt-") Then
                'Quilt 指定
                Try
                    Log("[ModPack] 整合包 Quilt 版本：" & Id)
                    QuiltVersion = Id.Replace("quilt-", "")
                    Exit For
                Catch ex As Exception
                    Log(ex, "读取整合包 Quilt 版本失败：" & Id)
                End Try
            End If
        Next
        '解压与配置文件
        Dim InstallTemp As String = PathTemp & "PackInstall\" & RandomInteger(0, 100000) & "\"
        Dim InstallLoaders As New List(Of LoaderBase)
        Dim OverrideHome As String = If(Json("overrides"), "")
        If OverrideHome <> "" Then
            InstallLoaders.Add(New LoaderTask(Of String, Integer)("解压整合包文件",
            Sub(Task As LoaderTask(Of String, Integer))
                UnpackFiles(InstallTemp, FileAddress, Task)
                Task.Progress = 0.5
                Dim OverridePath As String = InstallTemp & ArchiveBaseFolder & OverrideHome
                '复制结果
                If Directory.Exists(OverridePath) Then
                    CopyDirectory(OverridePath, PathMcFolder & "versions\" & VersionName)
                    Log($"[ModPack] 整合包 override 复制：{OverridePath} -> {PathMcFolder & "versions\" & VersionName}")
                Else
                    Log($"[ModPack] 整合包中未找到 overrides 文件夹：{OverridePath}")
                End If
                Task.Progress = 0.9
                '开启版本隔离
                WriteIni(PathMcFolder & "versions\" & VersionName & "\PCL\Setup.ini", "VersionArgumentIndie", 1)
            End Sub) With {
            .ProgressWeight = New FileInfo(FileAddress).Length / 1024 / 1024 / 6, .Block = False}) '每 6M 需要 1s
        End If
        '获取 Mod 列表
        Dim ModList As New List(Of Integer)
        Dim ModOptionalList As New List(Of Integer)
        For Each ModEntry In If(Json("files"), {})
            If ModEntry("projectID") Is Nothing OrElse ModEntry("fileID") Is Nothing Then
                Hint("某项 Mod 缺少必要信息，已跳过：" & ModEntry.ToString)
                Continue For
            End If
            ModList.Add(ModEntry("fileID"))
            If ModEntry("required") IsNot Nothing AndAlso Not ModEntry("required").ToObject(Of Boolean) Then ModOptionalList.Add(ModEntry("fileID"))
        Next
        If ModList.Any Then
            Dim ModDownloadLoaders As New List(Of LoaderBase)
            '获取 Mod 下载信息
            ModDownloadLoaders.Add(New LoaderTask(Of Integer, JArray)("获取 Mod 下载信息",
            Sub(Task As LoaderTask(Of Integer, JArray))
                Task.Output = GetJson(DlModRequest("https://api.curseforge.com/v1/mods/files", "POST", "{""fileIds"": [" & Join(ModList, ",") & "]}", "application/json"))("data")
                '如果文件已被删除，则 API 会跳过那一项
                If ModList.Count > Task.Output.Count Then Throw New Exception("整合包所需要的部分 Mod 版本已被 Mod 作者删除，因此无法完成整合包安装，请联系整合包作者更新整合包中的 Mod 版本")
            End Sub) With {.ProgressWeight = ModList.Count / 10}) '每 10 Mod 需要 1s
            '构造 NetFile
            ModDownloadLoaders.Add(New LoaderTask(Of JArray, List(Of NetFile))("构造 Mod 下载信息",
            Sub(Task As LoaderTask(Of JArray, List(Of NetFile)))
                Dim FileList As New Dictionary(Of Integer, NetFile)
                For Each ModJson In Task.Input
                    Dim Id As Integer = ModJson("id").ToObject(Of Integer)
                    '跳过重复的 Mod（疑似 CurseForge Bug）
                    If FileList.ContainsKey(Id) Then Continue For
                    '可选 Mod 提示
                    If ModOptionalList.Contains(Id) Then
                        If MyMsgBox("是否要下载整合包中的可选文件 " & ModJson("displayName").ToString & "？",
                                        "下载可选文件", "是", "否") = 2 Then
                            Continue For
                        End If
                    End If
                    '建立 CompFile
                    Dim File As New CompFile(ModJson, CompType.Mod)
                    If Not File.Available Then Continue For
                    '根据 modules 和文件名后缀判断资源类型
                    Dim TargetFolder As String
                    If ModJson("modules").Any Then 'modules 可能返回 null（#1006）
                        Dim ModuleNames = CType(ModJson("modules"), JArray).Select(Function(l) l("name").ToString).ToList
                        If ModuleNames.Contains("META-INF") OrElse ModuleNames.Contains("mcmod.info") OrElse
                           File.FileName.EndsWithF(".jar", True) Then
                            TargetFolder = "mods"
                        ElseIf ModuleNames.Contains("pack.mcmeta") Then
                            TargetFolder = "resourcepacks"
                        Else
                            TargetFolder = "shaderpacks"
                        End If
                    Else
                        TargetFolder = "mods"
                    End If
                    '实际的添加
                    FileList.Add(Id, File.ToNetFile($"{PathMcFolder}versions\{VersionName}\{TargetFolder}\"))
                    Task.Progress += 1 / (1 + ModList.Count)
                Next
                Task.Output = FileList.Values.ToList
            End Sub) With {.ProgressWeight = ModList.Count / 200, .Show = False}) '每 200 Mod 需要 1s
            '下载 Mod 文件
            ModDownloadLoaders.Add(New LoaderDownload("下载 Mod", New List(Of NetFile)) With {.ProgressWeight = ModList.Count * 1.5}) '每个 Mod 需要 1.5s
            '构造加载器
            InstallLoaders.Add(New LoaderCombo(Of Integer)("下载 Mod（主加载器）", ModDownloadLoaders) With
                {.Show = False, .ProgressWeight = ModDownloadLoaders.Sum(Function(l) l.ProgressWeight)})
        End If

        '构造加载器
        Dim Request As New McInstallRequest With {
            .TargetVersionName = VersionName,
            .TargetVersionFolder = $"{PathMcFolder}versions\{VersionName}\",
            .MinecraftName = Json("minecraft")("version").ToString,
            .ForgeVersion = ForgeVersion,
            .NeoForgeVersion = NeoForgeVersion,
            .FabricVersion = FabricVersion,
            .QuiltVersion = QuiltVersion
        }
        Dim MergeLoaders As List(Of LoaderBase) = McInstallLoader(Request, True)
        If MergeLoaders Is Nothing Then Return Nothing
        '构造 Libraries 加载器
        Dim LoadersLib As New List(Of LoaderBase)
        LoadersLib.Add(New LoaderTask(Of String, List(Of NetFile))("分析游戏支持库文件（副加载器）", Sub(Task As LoaderTask(Of String, List(Of NetFile))) Task.Output = McLibFix(New McVersion(VersionName))) With {.ProgressWeight = 1, .Show = False})
        LoadersLib.Add(New LoaderDownload("下载游戏支持库文件（副加载器）", New List(Of NetFile)) With {.ProgressWeight = 7, .Show = False})
        '构造总加载器
        Dim Loaders As New List(Of LoaderBase)
        Loaders.Add(New LoaderCombo(Of String)("整合包安装", InstallLoaders) With {.Show = False, .Block = False, .ProgressWeight = InstallLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("游戏安装", MergeLoaders) With {.Show = False, .ProgressWeight = MergeLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("下载游戏支持库文件", LoadersLib) With {.ProgressWeight = 8})
        Loaders.Add(New LoaderTask(Of String, String)("最终整理文件",
        Sub(Task As LoaderTask(Of String, String))
            '设置图标
            Dim VersionFolder As String = PathMcFolder & "versions\" & VersionName & "\"
            If Logo IsNot Nothing AndAlso File.Exists(Logo) Then
                File.Copy(Logo, VersionFolder & "PCL\Logo.png", True)
                WriteIni(VersionFolder & "PCL\Setup.ini", "Logo", "PCL\Logo.png")
                WriteIni(VersionFolder & "PCL\Setup.ini", "LogoCustom", "True")
                Log("[ModPack] 已设置整合包 Logo：" & Logo)
            End If
            '删除原始整合包文件
            For Each Target As String In {VersionFolder & "原始整合包.zip", VersionFolder & "原始整合包.mrpack"}
                If Not Setup.Get("ToolDownloadKeepModpack") AndAlso File.Exists(Target) Then
                    Log("[ModPack] 根据设置要求删除原始整合包文件：" & Target)
                    File.Delete(Target)
                End If
            Next
        End Sub) With {.ProgressWeight = 0.1, .Show = False})

        '重复任务检查
        Dim LoaderName As String = "CurseForge 整合包安装：" & VersionName & " "
        If LoaderTaskbar.Any(Function(l) l.Name = LoaderName) Then
            Hint("该整合包正在安装中！", HintType.Critical)
            Return Nothing
        End If

        '启动
        Dim Loader As New LoaderCombo(Of String)(LoaderName, Loaders) With {.OnStateChanged = AddressOf McInstallState}
        Loader.Start(Request.TargetVersionFolder)
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        If ShowRibble Then FrmMain.BtnExtraDownload.Ribble()
        Return Loader
    End Function

    'Modrinth
    Private Function InstallPackModrinth(FileAddress As String, Archive As Compression.ZipArchive, ArchiveBaseFolder As String, Optional VersionName As String = Nothing, Optional Logo As String = Nothing) As LoaderCombo(Of String)

        '读取 Json 文件
        Dim Json As JObject
        Try
            Json = GetJson(ReadFile(Archive.GetEntry(ArchiveBaseFolder & "modrinth.index.json").Open))
            If Json("dependencies") Is Nothing OrElse Json("dependencies")("minecraft") Is Nothing Then Throw New Exception("整合包未提供 Minecraft 版本信息")
        Catch ex As Exception
            Log(ex, "整合包安装信息存在问题", LogLevel.Hint)
            Return Nothing
        End Try
        '获取 Mod API 版本信息
        Dim MinecraftVersion As String = Nothing
        Dim ForgeVersion As String = Nothing
        Dim NeoForgeVersion As String = Nothing
        Dim FabricVersion As String = Nothing
        Dim QuiltVersion As String = Nothing
        For Each Entry As JProperty In If(Json("dependencies"), {})
            Select Case Entry.Name.ToLower
                Case "minecraft"
                    MinecraftVersion = Entry.Value.ToString
                Case "forge" 'eg. 14.23.5.2859 / 1.19-41.1.0
                    ForgeVersion = Entry.Value.ToString
                    Log("[ModPack] 整合包 Forge 版本：" & ForgeVersion)
                Case "neoforge", "neo-forge" 'eg. 20.6.98-beta
                    NeoForgeVersion = Entry.Value.ToString
                    Log("[ModPack] 整合包 NeoForge 版本：" & NeoForgeVersion)
                Case "fabric-loader" 'eg. 0.14.14
                    FabricVersion = Entry.Value.ToString
                    Log("[ModPack] 整合包 Fabric 版本：" & FabricVersion)
                Case "quilt-loader" 'eg. 0.26.0
                    QuiltVersion = Entry.Value.ToString
                    Log("[ModPack] 整合包 Quilt 版本：" & QuiltVersion)
                Case Else
                    Hint($"无法安装整合包，其中出现了未知的 Mod 加载器 {Entry.Name}（版本为 {Entry.Value.ToString}）！", HintType.Critical)
            End Select
        Next
        '获取版本名
        Dim ShowRibble As Boolean = VersionName Is Nothing
        If VersionName Is Nothing Then
            VersionName = If(Json("name"), "")
            Dim Validate As New ValidateFolderName(PathMcFolder & "versions")
            If Validate.Validate(VersionName) <> "" Then VersionName = ""
            If VersionName = "" Then VersionName = MyMsgBoxInput("输入版本名称", "", "", New ObjectModel.Collection(Of Validate) From {Validate})
            If String.IsNullOrEmpty(VersionName) Then Return Nothing
        End If
        '解压和配置文件
        Dim InstallTemp As String = PathTemp & "PackInstall\" & RandomInteger(0, 100000) & "\"
        Dim InstallLoaders As New List(Of LoaderBase)
        InstallLoaders.Add(New LoaderTask(Of String, Integer)("解压整合包文件",
        Sub(Task As LoaderTask(Of String, Integer))
            UnpackFiles(InstallTemp, FileAddress, Task)
            Task.Progress = 0.5
            '复制 overrides 文件夹和 client-overrides 文件夹
            If Directory.Exists(InstallTemp & ArchiveBaseFolder & "overrides") Then
                CopyDirectory(InstallTemp & ArchiveBaseFolder & "overrides", PathMcFolder & "versions\" & VersionName)
            Else
                Log("[ModPack] 整合包中未找到 override 目录，已跳过")
            End If
            Task.Progress = 0.8
            If Directory.Exists(InstallTemp & ArchiveBaseFolder & "client-overrides") Then
                CopyDirectory(InstallTemp & ArchiveBaseFolder & "client-overrides", PathMcFolder & "versions\" & VersionName)
            End If
            Task.Progress = 0.9
            '开启版本隔离
            WriteIni(PathMcFolder & "versions\" & VersionName & "\PCL\Setup.ini", "VersionArgumentIndie", 1)
        End Sub) With {.ProgressWeight = New FileInfo(FileAddress).Length / 1024 / 1024 / 6, .Block = False}) '每 6M 需要 1s
        '获取下载文件列表
        Dim FileList As New List(Of NetFile)
        For Each File In If(Json("files"), {})
            '检查是否需要该文件
            If File("env") IsNot Nothing Then
                Select Case File("env")("client").ToString
                    Case "optional"
                        If MyMsgBox("是否要下载整合包中的可选文件 " & GetFileNameFromPath(File("path").ToString) & "？",
                                    "下载可选文件", "是", "否") = 2 Then
                            Continue For
                        End If
                    Case "unsupported"
                        Continue For
                End Select
            End If
            '添加下载文件
            Dim Urls = File("downloads").Select(Function(t) t.ToString.Replace("://edge.forgecdn", "://media.forgecdn")).ToList
            Urls.AddRange(Urls.Select(Function(u) DlSourceModGet(u)).ToList)
            Urls = Urls.Distinct.ToList()
            FileList.Add(New NetFile(Urls, PathMcFolder & "versions\" & VersionName & "\" & File("path").ToString,
                New FileChecker(ActualSize:=File("fileSize").ToObject(Of Long), Hash:=File("hashes")("sha1").ToString), True))
        Next
        If FileList.Any Then
            InstallLoaders.Add(New LoaderDownload("下载额外文件", FileList) With {.ProgressWeight = FileList.Count * 1.5}) '每个 Mod 需要 1.5s
        End If

        '构造加载器
        Dim Request As New McInstallRequest With {
            .TargetVersionName = VersionName,
            .TargetVersionFolder = $"{PathMcFolder}versions\{VersionName}\",
            .MinecraftName = MinecraftVersion,
            .ForgeVersion = ForgeVersion,
            .NeoForgeVersion = NeoForgeVersion,
            .FabricVersion = FabricVersion,
            .QuiltVersion = QuiltVersion
        }
        Dim MergeLoaders As List(Of LoaderBase) = McInstallLoader(Request, True)
        If MergeLoaders Is Nothing Then Return Nothing
        '构造 Libraries 加载器
        Dim LoadersLib As New List(Of LoaderBase)
        LoadersLib.Add(New LoaderTask(Of String, List(Of NetFile))("分析游戏支持库文件（副加载器）", Sub(Task As LoaderTask(Of String, List(Of NetFile))) Task.Output = McLibFix(New McVersion(VersionName))) With {.ProgressWeight = 1, .Show = False})
        LoadersLib.Add(New LoaderDownload("下载游戏支持库文件（副加载器）", New List(Of NetFile)) With {.ProgressWeight = 7, .Show = False})
        '构造总加载器
        Dim Loaders As New List(Of LoaderBase)
        Loaders.Add(New LoaderCombo(Of String)("整合包安装", InstallLoaders) With {.Show = False, .Block = False, .ProgressWeight = InstallLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("游戏安装", MergeLoaders) With {.Show = False, .ProgressWeight = MergeLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("下载游戏支持库文件", LoadersLib) With {.ProgressWeight = 8})
        Loaders.Add(New LoaderTask(Of String, String)("最终整理文件",
        Sub(Task As LoaderTask(Of String, String))
            '设置图标
            Dim VersionFolder As String = PathMcFolder & "versions\" & VersionName & "\"
            If Logo IsNot Nothing AndAlso File.Exists(Logo) Then
                File.Copy(Logo, VersionFolder & "PCL\Logo.png", True)
                WriteIni(VersionFolder & "PCL\Setup.ini", "Logo", "PCL\Logo.png")
                WriteIni(VersionFolder & "PCL\Setup.ini", "LogoCustom", "True")
                Log("[ModPack] 已设置整合包 Logo：" & Logo)
            End If
            '删除原始整合包文件
            For Each Target As String In {VersionFolder & "原始整合包.zip", VersionFolder & "原始整合包.mrpack"}
                If Not Setup.Get("ToolDownloadKeepModpack") AndAlso File.Exists(Target) Then
                    Log("[ModPack] 根据设置要求删除原始整合包文件：" & Target)
                    File.Delete(Target)
                End If
            Next
        End Sub) With {.ProgressWeight = 0.1, .Show = False})

        '重复任务检查
        Dim LoaderName As String = "Modrinth 整合包安装：" & VersionName & " "
        If LoaderTaskbar.Any(Function(l) l.Name = LoaderName) Then
            Hint("该整合包正在安装中！", HintType.Critical)
            Return Nothing
        End If

        '启动
        Dim Loader As New LoaderCombo(Of String)(LoaderName, Loaders) With {.OnStateChanged = AddressOf McInstallState}
        Loader.Start(Request.TargetVersionFolder)
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        If ShowRibble Then FrmMain.BtnExtraDownload.Ribble()
        Return Loader

    End Function

    'HMCL
    Private Function InstallPackHMCL(FileAddress As String, Archive As Compression.ZipArchive, ArchiveBaseFolder As String) As LoaderCombo(Of String)
        '读取 Json 文件
        Dim Json As JObject
        Try
            Json = GetJson(ReadFile(Archive.GetEntry(ArchiveBaseFolder & "modpack.json").Open, Encoding.UTF8))
        Catch ex As Exception
            Log(ex, "整合包安装信息存在问题", LogLevel.Hint)
            Return Nothing
        End Try
        '获取版本名
        Dim VersionName As String = If(Json("name"), "")
        Dim Validate As New ValidateFolderName(PathMcFolder & "versions")
        If Validate.Validate(VersionName) <> "" Then VersionName = ""
        If VersionName = "" Then VersionName = MyMsgBoxInput("输入版本名称", "", "", New ObjectModel.Collection(Of Validate) From {Validate})
        If String.IsNullOrEmpty(VersionName) Then Return Nothing
        '解压与配置文件
        Dim InstallTemp As String = PathTemp & "PackInstall\" & RandomInteger(0, 100000) & "\"
        Dim InstallLoaders As New List(Of LoaderBase)
        InstallLoaders.Add(New LoaderTask(Of String, Integer)("解压整合包文件",
        Sub(Task As LoaderTask(Of String, Integer))
            UnpackFiles(InstallTemp, FileAddress, Task)
            Task.Progress = 0.5
            '复制结果
            If Directory.Exists(InstallTemp & ArchiveBaseFolder & "minecraft") Then
                CopyDirectory(InstallTemp & ArchiveBaseFolder & "minecraft", PathMcFolder & "versions\" & VersionName)
            Else
                Log("[ModPack] 整合包中未找到 minecraft overrides 目录，已跳过")
            End If
            Task.Progress = 0.9
            '开启版本隔离
            WriteIni(PathMcFolder & "versions\" & VersionName & "\PCL\Setup.ini", "VersionArgumentIndie", 1)
        End Sub) With {.ProgressWeight = New FileInfo(FileAddress).Length / 1024 / 1024 / 6, .Block = False}) '每 6M 需要 1s
        '构造加载器
        If Json("gameVersion") Is Nothing Then
            Hint("该整合包未提供游戏版本信息，无法安装！", HintType.Critical)
            Return Nothing
        End If
        Dim Request As New McInstallRequest With {
            .TargetVersionName = VersionName,
            .TargetVersionFolder = $"{PathMcFolder}versions\{VersionName}\",
            .MinecraftName = Json("gameVersion").ToString
        }
        Dim MergeLoaders As List(Of LoaderBase) = McInstallLoader(Request, True)
        If MergeLoaders Is Nothing Then Return Nothing
        '构造 Libraries 加载器（为了使得 Mods 下载结束后再构造，这样才会下载 JumpLoader 文件）
        Dim LoadersLib As New List(Of LoaderBase)
        LoadersLib.Add(New LoaderTask(Of String, String)("重命名版本 Json（副加载器）",
        Sub()
            Dim RealFileName As String = PathMcFolder & "versions\" & VersionName & "\" & VersionName & ".json"
            Dim OldFileName As String = PathMcFolder & "versions\" & VersionName & "\pack.json"
            If File.Exists(OldFileName) Then
                '修改 id
                Dim FileJson = GetJson(ReadFile(OldFileName))
                FileJson("id") = VersionName
                '替换文件
                File.Delete(OldFileName)
                WriteFile(RealFileName, FileJson.ToString)
                Log("[ModPack] 已重命名版本 Json：" & RealFileName)
            End If
        End Sub) With {.ProgressWeight = 0.1, .Show = False})
        LoadersLib.Add(New LoaderTask(Of String, List(Of NetFile))("分析游戏支持库文件（副加载器）", Sub(Task As LoaderTask(Of String, List(Of NetFile))) Task.Output = McLibFix(New McVersion(VersionName))) With {.ProgressWeight = 1, .Show = False})
        LoadersLib.Add(New LoaderDownload("下载游戏支持库文件（副加载器）", New List(Of NetFile)) With {.ProgressWeight = 7, .Show = False})
        '构造总加载器
        Dim Loaders As New List(Of LoaderBase) From {
            New LoaderCombo(Of String)("游戏安装", MergeLoaders) With {.Show = False, .Block = False, .ProgressWeight = MergeLoaders.Sum(Function(l) l.ProgressWeight)},
            New LoaderCombo(Of String)("整合包安装", InstallLoaders) With {.Show = False, .ProgressWeight = InstallLoaders.Sum(Function(l) l.ProgressWeight)},
            New LoaderCombo(Of String)("下载游戏支持库文件", LoadersLib) With {.ProgressWeight = 8}
        }

        '重复任务检查
        Dim LoaderName As String = "HMCL 整合包安装：" & VersionName & " "
        If LoaderTaskbar.Any(Function(l) l.Name = LoaderName) Then
            Hint("该整合包正在安装中！", HintType.Critical)
            Return Nothing
        End If

        '启动
        Dim Loader As New LoaderCombo(Of String)(LoaderName, Loaders) With {.OnStateChanged = AddressOf McInstallState}
        'If Archive IsNot Nothing Then Archive.Dispose() '解除占用，以免在加载器中触发 “正由另一进程使用，因此该进程无法访问此文件”
        Loader.Start(Request.TargetVersionFolder)
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        FrmMain.BtnExtraDownload.Ribble()
        Return Loader
    End Function

    'MMC
    Private Function InstallPackMMC(FileAddress As String, Archive As Compression.ZipArchive, ArchiveBaseFolder As String) As LoaderCombo(Of String)
        '读取 Json 文件
        Dim PackJson As JObject, PackInstance As String
        Try
            PackJson = GetJson(ReadFile(Archive.GetEntry(ArchiveBaseFolder & "mmc-pack.json").Open, Encoding.UTF8))
            PackInstance = ReadFile(Archive.GetEntry(ArchiveBaseFolder & "instance.cfg").Open, Encoding.UTF8)
        Catch ex As Exception
            Log(ex, "整合包安装信息存在问题", LogLevel.Hint)
            Return Nothing
        End Try
        '获取版本名
        Dim VersionName As String = If(RegexSeek(PackInstance, "(?<=\nname\=)[^\n]+"), "")
        Dim Validate As New ValidateFolderName(PathMcFolder & "versions")
        If Validate.Validate(VersionName) <> "" Then VersionName = ""
        If VersionName = "" Then VersionName = MyMsgBoxInput("输入版本名称", "", "", New ObjectModel.Collection(Of Validate) From {Validate})
        If String.IsNullOrEmpty(VersionName) Then Return Nothing
        '解压、配置设置文件
        Dim InstallTemp As String = $"{PathTemp}PackInstall\{RandomInteger(0, 100000)}\"
        Dim SetupFile As String = $"{PathMcFolder}versions\{VersionName}\PCL\Setup.ini"
        Dim InstallLoaders As New List(Of LoaderBase)
        InstallLoaders.Add(New LoaderTask(Of String, Integer)("解压整合包文件",
        Sub(Task As LoaderTask(Of String, Integer))
            UnpackFiles(InstallTemp, FileAddress, Task)
            Task.Progress = 0.5
            '复制结果
            If Directory.Exists(InstallTemp & ArchiveBaseFolder & ".minecraft") Then
                CopyDirectory(InstallTemp & ArchiveBaseFolder & ".minecraft", PathMcFolder & "versions\" & VersionName)
            Else
                Log("[ModPack] 整合包中未找到 overrides .minecraft 目录，已跳过")
            End If
            Task.Progress = 0.9
            '开启版本隔离
            WriteIni(SetupFile, "VersionArgumentIndie", 1)
            '读取 MMC 设置文件（#2655）
            Try
                Dim MMCSetupFile As String = InstallTemp & ArchiveBaseFolder & "instance.cfg"
                If File.Exists(MMCSetupFile) Then
                    '将其中的等号替换为冒号，以符合 ini 文件格式
                    WriteFile(MMCSetupFile, ReadFile(MMCSetupFile).Replace("=", ":"))
                    If ReadIni(MMCSetupFile, "OverrideCommands", False) Then
                        Dim PreLaunchCommand As String = ReadIni(MMCSetupFile, "PreLaunchCommand")
                        If PreLaunchCommand <> "" Then
                            PreLaunchCommand = PreLaunchCommand.Replace("\""", """").
                                Replace("$INST_JAVA", "{java}javaw.exe").
                                Replace("$INST_MC_DIR\", "{minecraft}").Replace("$INST_MC_DIR", "{minecraft}").
                                Replace("$INST_DIR\", "{verpath}").Replace("$INST_DIR", "{verpath}").
                                Replace("$INST_ID", "{name}").Replace("$INST_NAME", "{name}")
                            WriteIni(SetupFile, "VersionAdvanceRun", PreLaunchCommand)
                            Log("[ModPack] 迁移 MultiMC 版本独立设置：启动前执行命令：" & PreLaunchCommand)
                        End If
                    End If
                    If ReadIni(MMCSetupFile, "JoinServerOnLaunch", False) Then
                        Dim ServerAddress As String = ReadIni(MMCSetupFile, "JoinServerOnLaunchAddress").Replace("\""", """")
                        WriteIni(SetupFile, "VersionServerEnter", ServerAddress)
                        Log("[ModPack] 迁移 MultiMC 版本独立设置：自动进入服务器：" & ServerAddress)
                    End If
                    If ReadIni(MMCSetupFile, "IgnoreJavaCompatibility", False) Then
                        WriteIni(SetupFile, "VersionAdvanceJava", True)
                        Log("[ModPack] 迁移 MultiMC 版本独立设置：忽略 Java 兼容性警告")
                    End If
                    Dim Logo As String = ReadIni(MMCSetupFile, "iconKey", "")
                    If Logo <> "" AndAlso File.Exists($"{InstallTemp}{ArchiveBaseFolder}{Logo}.png") Then
                        WriteIni(SetupFile, "LogoCustom", True)
                        WriteIni(SetupFile, "Logo", "PCL\Logo.png")
                        CopyFile($"{InstallTemp}{ArchiveBaseFolder}{Logo}.png", $"{PathMcFolder}versions\{VersionName}\PCL\Logo.png")
                        Log($"[ModPack] 迁移 MultiMC 版本独立设置：版本图标（{Logo}.png）")
                    End If
                End If
            Catch ex As Exception
                Log(ex, $"读取 MMC 配置文件失败（{InstallTemp}{ArchiveBaseFolder}instance.cfg）")
            End Try
        End Sub) With {.ProgressWeight = New FileInfo(FileAddress).Length / 1024 / 1024 / 6, .Block = False}) '每 6M 需要 1s
        '构造版本安装请求
        If PackJson("components") Is Nothing Then
            Hint("该整合包未提供游戏版本信息，无法安装！", HintType.Critical)
            Return Nothing
        End If
        Dim Request As New McInstallRequest With {.TargetVersionName = VersionName, .TargetVersionFolder = $"{PathMcFolder}versions\{VersionName}\"}
        For Each Component In PackJson("components")
            Select Case If(Component("uid"), "").ToString
                Case "org.lwjgl"
                    Log("[ModPack] 已跳过 LWJGL 项")
                Case "net.minecraft"
                    Request.MinecraftName = Component("version")
                Case "net.minecraftforge"
                    Request.ForgeVersion = Component("version")
                Case "net.neoforged"
                    Request.NeoForgeVersion = Component("version")
                Case "net.fabricmc.fabric-loader"
                    Request.FabricVersion = Component("version")
                Case "org.quiltmc.quilt-loader"
                    Request.QuiltVersion = Component("version")
            End Select
        Next
        '构造加载器
        Dim MergeLoaders As List(Of LoaderBase) = McInstallLoader(Request, True)
        If MergeLoaders Is Nothing Then Return Nothing
        '构造 Libraries 加载器
        Dim LoadersLib As New List(Of LoaderBase)
        LoadersLib.Add(New LoaderTask(Of String, List(Of NetFile))("分析游戏支持库文件（副加载器）", Sub(Task As LoaderTask(Of String, List(Of NetFile))) Task.Output = McLibFix(New McVersion(VersionName))) With {.ProgressWeight = 1, .Show = False})
        LoadersLib.Add(New LoaderDownload("下载游戏支持库文件（副加载器）", New List(Of NetFile)) With {.ProgressWeight = 7, .Show = False})
        '构造总加载器
        Dim Loaders As New List(Of LoaderBase)
        Loaders.Add(New LoaderCombo(Of String)("游戏安装", MergeLoaders) With {.Show = False, .Block = False, .ProgressWeight = MergeLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("整合包安装", InstallLoaders) With {.Show = False, .ProgressWeight = InstallLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("下载游戏支持库文件", LoadersLib) With {.ProgressWeight = 8})

        '重复任务检查
        Dim LoaderName As String = "MMC 整合包安装：" & VersionName & " "
        If LoaderTaskbar.Any(Function(l) l.Name = LoaderName) Then
            Hint("该整合包正在安装中！", HintType.Critical)
            Return Nothing
        End If

        '启动
        Dim Loader As New LoaderCombo(Of String)(LoaderName, Loaders) With {.OnStateChanged = AddressOf McInstallState}
        Loader.Start(Request.TargetVersionFolder)
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        FrmMain.BtnExtraDownload.Ribble()
        Return Loader
    End Function

    'MCBBS
    Private Function InstallPackMCBBS(FileAddress As String, Archive As Compression.ZipArchive, ArchiveBaseFolder As String,
                                      Optional VersionName As String = Nothing) As LoaderCombo(Of String)
        '读取 Json 文件
        Dim Json As JObject
        Try
            Dim Entry = If(Archive.GetEntry(ArchiveBaseFolder & "mcbbs.packmeta"), Archive.GetEntry(ArchiveBaseFolder & "manifest.json"))
            Json = GetJson(ReadFile(Entry.Open, Encoding.UTF8))
        Catch ex As Exception
            Log(ex, "整合包安装信息存在问题", LogLevel.Hint)
            Return Nothing
        End Try
        '获取版本名
        Dim ShowRibble As Boolean = VersionName Is Nothing
        If VersionName Is Nothing Then
            VersionName = If(Json("name"), "")
            Dim Validate As New ValidateFolderName(PathMcFolder & "versions")
            If Validate.Validate(VersionName) <> "" Then VersionName = ""
            If VersionName = "" Then VersionName = MyMsgBoxInput("输入版本名称", "", "", New ObjectModel.Collection(Of Validate) From {Validate})
            If String.IsNullOrEmpty(VersionName) Then Return Nothing
        End If
        '解压与配置文件
        Dim InstallTemp As String = PathTemp & "PackInstall\" & RandomInteger(0, 100000) & "\"
        Dim InstallLoaders As New List(Of LoaderBase)
        InstallLoaders.Add(New LoaderTask(Of String, Integer)("解压整合包文件",
        Sub(Task As LoaderTask(Of String, Integer))
            UnpackFiles(InstallTemp, FileAddress, Task)
            Task.Progress = 0.5
            '复制结果
            If Directory.Exists(InstallTemp & ArchiveBaseFolder & "overrides") Then
                CopyDirectory(InstallTemp & ArchiveBaseFolder & "overrides", PathMcFolder & "versions\" & VersionName)
            Else
                Log("[ModPack] 整合包中未找到 overrides 目录，已跳过")
            End If
            Task.Progress = 0.9
            '开启版本隔离
            WriteIni(PathMcFolder & "versions\" & VersionName & "\PCL\Setup.ini", "VersionArgumentIndie", 1)
        End Sub) With {.ProgressWeight = New FileInfo(FileAddress).Length / 1024 / 1024 / 6, .Block = False}) '每 6M 需要 1s
        If Json("launchInfo") Is Nothing Then
            Log("[ModPack] 整合包中未找到启动信息，已跳过")
        Else
            Dim launchArgument As String = String.Join(" ", Json("launchInfo")("launchArgument"))
            Dim javaArgument As String = String.Join(" ", Json("launchInfo")("javaArgument"))
            WriteIni(PathMcFolder & "versions\" & VersionName & "\PCL\Setup.ini", "VersionAdvanceJvm", javaArgument)
            WriteIni(PathMcFolder & "versions\" & VersionName & "\PCL\Setup.ini", "VersionAdvanceGame", launchArgument)
        End If                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        
        '构造加载器
        If Json("addons") Is Nothing Then
            Hint("该整合包未提供游戏版本附加信息，无法安装！", HintType.Critical)
            Return Nothing
        End If
        Dim Addons As New Dictionary(Of String, String)
        For Each Entry In Json("addons")
            Addons.Add(Entry("id"), Entry("version"))
        Next
        If Not Addons.ContainsKey("game") Then
            Hint("该整合包未提供游戏版本信息，无法安装！", HintType.Critical)
            Return Nothing
        End If
        Dim Request As New McInstallRequest With {
            .TargetVersionName = VersionName,
            .TargetVersionFolder = $"{PathMcFolder}versions\{VersionName}\",
            .MinecraftName = Addons("game"),
            .OptiFineVersion = If(Addons.ContainsKey("optifine"), Addons("optifine"), Nothing),
            .ForgeVersion = If(Addons.ContainsKey("forge"), Addons("forge"), Nothing),
            .NeoForgeVersion = If(Addons.ContainsKey("neoforge"), Addons("neoforge"), Nothing),
            .FabricVersion = If(Addons.ContainsKey("fabric"), Addons("fabric"), Nothing),
            .QuiltVersion = If(Addons.ContainsKey("quilt"), Addons("quilt"), Nothing)
        }
        Dim MergeLoaders As List(Of LoaderBase) = McInstallLoader(Request, True)
        If MergeLoaders Is Nothing Then Return Nothing
        '构造 Libraries 加载器
        Dim LoadersLib As New List(Of LoaderBase)
        LoadersLib.Add(New LoaderTask(Of String, List(Of NetFile))("分析游戏支持库文件（副加载器）", Sub(Task As LoaderTask(Of String, List(Of NetFile))) Task.Output = McLibFix(New McVersion(VersionName))) With {.ProgressWeight = 1, .Show = False})
        LoadersLib.Add(New LoaderDownload("下载游戏支持库文件（副加载器）", New List(Of NetFile)) With {.ProgressWeight = 7, .Show = False})
        '构造总加载器
        Dim Loaders As New List(Of LoaderBase)
        Loaders.Add(New LoaderCombo(Of String)("游戏安装", MergeLoaders) With {.Show = False, .Block = False, .ProgressWeight = MergeLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("整合包安装", InstallLoaders) With {.Show = False, .ProgressWeight = InstallLoaders.Sum(Function(l) l.ProgressWeight)})
        Loaders.Add(New LoaderCombo(Of String)("下载游戏支持库文件", LoadersLib) With {.ProgressWeight = 8})

        '重复任务检查
        Dim LoaderName As String = "MCBBS 整合包安装：" & VersionName & " "
        If LoaderTaskbar.Any(Function(l) l.Name = LoaderName) Then
            Hint("该整合包正在安装中！", HintType.Critical)
            Return Nothing
        End If

        '启动
        Dim Loader As New LoaderCombo(Of String)(LoaderName, Loaders) With {.OnStateChanged = AddressOf McInstallState}
        'If Archive IsNot Nothing Then Archive.Dispose() '解除占用，以免在加载器中触发 “正由另一进程使用，因此该进程无法访问此文件”
        Loader.Start(Request.TargetVersionFolder)
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        If ShowRibble Then FrmMain.BtnExtraDownload.Ribble()
        Return Loader
    End Function

    '普通压缩包
    Private Function InstallPackCompress(FileAddress As String, ArchiveBaseFolder As String) As LoaderCombo(Of String)
        MyMsgBox("请在接下来打开的窗口中选择安装目标文件夹，它必须是一个空文件夹。", "安装提示", "继续", ForceWait:=True)
        '获取解压路径
        Dim TargetFolder As String = SelectFolder("选择安装目标（必须是一个空文件夹）")
        If String.IsNullOrEmpty(TargetFolder) Then Return Nothing
        If TargetFolder.Contains("!") OrElse TargetFolder.Contains(";") Then Hint("Minecraft 文件夹路径中不能含有感叹号或分号！", HintType.Critical) : Return Nothing
        If Directory.GetFileSystemEntries(TargetFolder).Length > 0 Then Hint("请选择一个空文件夹作为安装目标！", HintType.Critical) : Return Nothing
        '要求显示名称
        Dim NewName As String = MyMsgBoxInput("输入显示名称", "输入该文件夹在左边栏列表中显示的名称。", GetFolderNameFromPath(TargetFolder),
                                              New ObjectModel.Collection(Of Validate) From {New ValidateNullOrWhiteSpace, New ValidateLength(1, 30), New ValidateExcept({">", "|"})})
        If String.IsNullOrWhiteSpace(NewName) Then Return Nothing
        '解压
        Dim Loader As New LoaderCombo(Of String)("安装压缩包", {
             New LoaderTask(Of String, Integer)("安装压缩包",
             Sub()
                 UnpackFiles(TargetFolder, FileAddress, Nothing)
                 PageSelectLeft.AddFolder(TargetFolder, NewName, False) '加入文件夹列表
             End Sub)
        })
        Loader.Start()
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        FrmMain.BtnExtraDownload.Ribble()
        Return Loader
    End Function

#End Region
#End Region

#Region "导出"
    Private ExpTempDir As String = PathTemp & "PackExport\"
    ''' <summary>
    ''' 整合包类型。
    ''' </summary>
    Public Enum ModpackType
        Modrinth
    End Enum
    ''' <summary>
    ''' 导出整合包的选项。
    ''' </summary>
    Public Class ExportOptions
        ''' <summary>
        ''' 要导出的版本。
        ''' </summary>
        Public Version As McVersion
        ''' <summary>
        ''' 要保存整合包的位置。
        ''' </summary>
        Public Dest As String
        ''' <summary>
        ''' 是否包括 PCL 文件。
        ''' </summary>
        Public IncludePCL As Boolean
        ''' <summary>
        ''' 整合包类型。
        ''' </summary>
        Public Type As ModpackType
        ''' <summary>
        ''' 要保留到整合包的文件。
        ''' </summary>
        Public Additional As String()
        ''' <summary>
        ''' 是否需要保留 PCL 全局设置。仅在包括 PCL 时有效。
        ''' </summary>
        Public PCLSetupGlobal As Boolean = True
        ''' <summary>
        ''' 整合包名称。
        ''' </summary>
        Public Name As String = ""
        ''' <summary>
        ''' 整合包描述。
        ''' </summary>
        Public Desc As String = ""
        ''' <summary>
        ''' 版本号。
        ''' </summary>
        Public VerID As String = ""
        Public Sub New(Version As McVersion,
                       Dest As String,
                       IncludePCL As Boolean,
                       Additional As String(),
                       Optional PCLSetupGlobal As Boolean = True,
                       Optional Name As String = "",
                       Optional Desc As String = "",
                       Optional VerID As String = "")
            Me.Version = Version
            Me.Dest = Dest
            Me.IncludePCL = IncludePCL
            Me.Additional = Additional
            Me.PCLSetupGlobal = PCLSetupGlobal
            Me.Name = Name
            Me.Desc = Desc
            Me.VerID = VerID
        End Sub
    End Class
    ''' <summary>
    ''' 导出整合包。不阻塞。
    ''' </summary>
    Public Sub ModpackExport(Options As ExportOptions)
        Hint("正在导出……")
        RunInNewThread(
            Sub()
                If ModpackExportBlocking(Options) Then
                    Hint("导出成功！", HintType.Finish)
                    OpenExplorer($"/select,""{Options.Dest}""")
                End If
            End Sub, "Modpack Export")
    End Sub
    Private Function ModpackExportBlocking(Options As ExportOptions) As Boolean
        If Options.IncludePCL Then
            Return ExportCompressed(Options.Version, Options.Dest, Options.Additional, Options.Name, Options.VerID, Options.PCLSetupGlobal)
        End If
        Return ExportModrinth(Options.Version, Options.Dest, Options.Additional, Options.Name, Options.VerID)
    End Function

#Region "冗余"
    Private VersionRedundant As String() = {"screenshots", "backups", "command_history\.txt", '个人文件
        ".*-natives", "server-resource-packs", "user.*cache\.json", "\.optifine", "\.fabric", "\.mixin\.out", '缓存
        ".*\.jar", "downloads", "realms_persistence.json", "\$\{natives_directory\}", "essential", '可联网更新
        "logs", "crash-reports", ".*\.log", "debug", '日志
        ".*\.dat_old", ".*\.old", '备份
        "\$\{quickPlayPath\}", '服务器
        "\.replay_cache", "replay_recordings", "replay_videos", 'ReplayMod
        "irisUpdateInfo\.json", 'Iris
        "modernfix", 'ModernFix 模组
        "modtranslations", 'Mod 翻译
        "schematics", 'schematics 模组
        ".*\.BakaCoreInfo", 'BakaXL 配置
        "hmclversion\.cfg", "log4j2\.xml" 'HMCL 配置
    }
    Public Function IsVerRedundant(FilePath As String) As Boolean
        FilePath = FilePath.Replace("/", "\")
        For Each regex In VersionRedundant
            If RegexCheck(GetFileNameFromPath(FilePath), regex, RegularExpressions.RegexOptions.IgnoreCase) Then Return True
            If FilePath.EndsWithF("\journeymap\data\") OrElse
                FilePath.EndsWithF("\journeymap\data") OrElse
                FilePath.EndsWithF("\mods\.connector") Then
                Return True
            End If
        Next
        Return False
    End Function
    Private MustExport As String() = {
        "mods", "PCL", ".*\.json"
    }
    Public Function IsMustExport(FileName As String) As Boolean
        For Each regex In MustExport
            If RegexCheck(FileName, regex, RegularExpressions.RegexOptions.IgnoreCase) Then Return True
        Next
        Return False
    End Function
#End Region

#Region "不同类型整合包的导出方法"
    Private Function ExportModrinth(Version As McVersion, DestPath As String, Additional As String(), Name As String, VerID As String) As Boolean
        Try
            Log($"[Export] 导出整合包（Modrinth）：{Version.Path} -> {DestPath}，额外版本文件 {If(Additional Is Nothing OrElse Not Additional.Any, "不导出", Additional.Join(", "))}")
            Dim tempDir As String = $"{ExpTempDir}{GetUuid()}\"
            Log($"[Export] 缓存文件夹：{tempDir}")
            Directory.CreateDirectory(tempDir)

            '步骤 1：从 Modrinth 获取 Mod 工程信息，得到 URL
            Dim Mods As New List(Of McMod)
            If Directory.Exists(Version.Path & "mods\") Then
                For Each m In Directory.EnumerateFiles(Version.Path & "mods\")
                    If m.EndsWithF(".jar") Then Mods.Add(New McMod(m))
                Next
            End If

            '从 Modrinth 获取信息
            Dim ModrinthHashes = Mods.Select(Function(m) m.ModrinthHash).ToList()
            Dim ModrinthVersion = CType(GetJson(NetRequestRetry("https://api.modrinth.com/v2/version_files", "POST",
    $"{{""hashes"": [""{ModrinthHashes.Join(""",""")}""], ""algorithm"": ""sha1""}}", "application/json")), JObject)
            Log($"[Export] 从 Modrinth 获取到 {ModrinthVersion.Count} 个本地 Mod 的对应信息")

            Dim ModrinthMapping As New Dictionary(Of String, JObject) 'Modrinth 获取到的 Mod
            Dim ModrinthFailed As New List(Of McMod) 'Modrinth 获取失败的 Mod
            For Each Entry In Mods
                If (Not ModrinthVersion.ContainsKey(Entry.ModrinthHash)) OrElse
                   (ModrinthVersion(Entry.ModrinthHash)("files")(0)("hashes")("sha1") <> Entry.ModrinthHash) Then
                    ModrinthFailed.Add(Entry)
                    Continue For
                End If
                ModrinthMapping.Add(Entry.FileName, ModrinthVersion(Entry.ModrinthHash)("files")(0))
            Next

            '步骤 2：把获取失败的 Mod 从 CurseForge 继续获取
            Dim CurseForgeHashes = ModrinthFailed.Select(Function(m) m.CurseForgeHash).ToList()
            Dim CurseForgeRaw = CType(CType(GetJson(NetRequestRetry("https://api.curseforge.com/v1/fingerprints/432/", "POST",
                                    $"{{""fingerprints"": [{CurseForgeHashes.Join(",")}]}}", "application/json")), JObject)("data")("exactMatches"), JContainer)
            Log($"[Export] 从 CurseForge 获取到 {CurseForgeRaw.Count} 个本地 Mod 的对应信息")


            Dim CurseForgeMapping As New Dictionary(Of String, JObject) 'CurseForge 获取到的 Mod
            For Each m In CurseForgeRaw
                Dim hash As String = ""
                For Each h In m("file")("hashes") '获取 Modrinth Hash
                    If h("algo").ToString = 1 Then
                        hash = h("value")
                        Exit For
                    End If
                Next
                If String.IsNullOrEmpty(hash) OrElse String.IsNullOrEmpty(m("file")("downloadUrl")) Then Continue For

                m("file")("ModrinthHash") = hash
                CurseForgeMapping.Add(m("file")("fileName"), m("file"))
                For Each file In ModrinthFailed
                    If file.ModrinthHash = hash Then
                        ModrinthFailed.Remove(file)
                        Exit For
                    End If
                Next
            Next

            '步骤 3：写入 Json 文件
            '获取作为检查目标的加载器和版本
            Dim ModLoaders = GetTargetModLoaders()
            Dim McVersion = Version.Version.McName
            Log($"[Export] 目标加载器：{ModLoaders.Join("/")}，版本：{McVersion}")

            Dim files As New JArray
            For Each m In ModrinthMapping
                files.Add(New JObject From {
                    {"path", $"mods/{m.Key}"},
                    {"hashes", m.Value("hashes")},
                    {"env", New JObject From {{"client", "required"}, {"server", "required"}}},
                    {"downloads", New JArray From {m.Value("url")}},
                    {"fileSize", m.Value("size")}
                })
            Next
            For Each m In CurseForgeMapping
                files.Add(New JObject From {
                    {"path", $"mods/{m.Key}"},
                    {"hashes", New JObject From {{"sha1", m.Value("ModrinthHash").ToString}}},
                    {"env", New JObject From {{"client", "required"}, {"server", "required"}}},
                    {"downloads", New JArray From {m.Value("downloadUrl")}},
                    {"fileSize", m.Value("fileLength")}
                })
            Next

            Dim depend As New JObject From {{"minecraft", McVersion}}
            If Version.Version.HasForge Then depend.Add("forge", Version.Version.ForgeVersion)
            If Version.Version.HasFabric Then depend.Add("fabric-loader", Version.Version.FabricVersion)
            If Version.Version.HasNeoForge Then depend.Add("neoforge", Version.Version.NeoForgeVersion)

            Dim json As New JObject From {
                {"game", "minecraft"},
                {"formatVersion", 1},
                {"versionId", VerID},
                {"name", If(String.IsNullOrEmpty(Name), Version.Name, Name)},
                {"summary", Version.Info},
                {"files", files},
                {"dependencies", depend}
            }

            File.WriteAllText(tempDir & "modrinth.index.json", json.ToString)

            '步骤 4：将获取不到的保存到 overrides 目录
            For Each m In ModrinthFailed
                CopyFile(m.Path, tempDir & "overrides\mods\" & m.FileName)
            Next

            '额外文件
            For Each p In Additional
                If String.IsNullOrWhiteSpace(p) Then Continue For '传空字串进去会直接把整个版本文件夹拷过去
                If Not p.StartsWithF(Version.Path, IgnoreCase:=True) Then Continue For
                Dim relative As String = p.Replace(Version.Path, "")
                If File.Exists(p) AndAlso Not IsVerRedundant(p) Then
                    CopyFile(p, $"{tempDir}overrides\{relative}")
                End If
            Next

            If File.Exists(DestPath) Then File.Delete(DestPath) '选择文件的时候已经确认了要替换
            ZipFile.CreateFromDirectory(tempDir, DestPath)
            DeleteDirectory(tempDir)
            Return True
        Catch ex As Exception
            Log(ex, "导出整合包失败", LogLevel.Msgbox)
            Return False
        End Try
    End Function
    Private Function ExportCompressed(Version As McVersion, DestPath As String, Additional As String(), Name As String, VerID As String, PCLSetupGlobal As Boolean) As Boolean
        Try
            Log($"[Export] 导出整合包（含启动器）：{Version.Path} -> {DestPath}，额外版本文件 {Additional.Count} 个，全局设置 {If(PCLSetupGlobal, "导出", "不导出")}")
            Dim tempDir As String = $"{ExpTempDir}{GetUuid()}\"
            Log($"[Export] 最终压缩包的缓存文件夹：{tempDir}")
            Directory.CreateDirectory(tempDir)

            Log("[Export] 开始导出 Modrinth 整合包")
            ExportModrinth(Version, $"{tempDir}modpack.mrpack", Additional, Name, VerID)

            Log($"[Export] 正在复制 PCL 本体")
            CopyFile(PathWithName, tempDir & GetFileNameFromPath(PathWithName))

            If PCLSetupGlobal Then
                Log($"[Export] 正在复制 PCL 全局配置")
                CopyFile(Path & "PCL\Setup.ini", tempDir & "PCL\Setup.ini")
            End If

            If File.Exists(DestPath) Then File.Delete(DestPath) '选择文件的时候已经确认了要替换
            ZipFile.CreateFromDirectory(tempDir, DestPath)
            DeleteDirectory(tempDir)
            Return True
        Catch ex As Exception
            Log(ex, "导出整合包失败", LogLevel.Msgbox)
            Return False
        End Try
    End Function
#End Region

#End Region

End Module
