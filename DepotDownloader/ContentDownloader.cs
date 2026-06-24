// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDownloader
{
    class ContentDownloaderException(string value) : Exception(value)
    {
    }

    static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "public";

        public static DownloadConfig Config = new();

        private static Steam3Session steam3;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string CONFIG_DIR = ".DepotDownloader";
        private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

        private static readonly FrozenSet<EWorkshopFileType> SupportedWorkshopFileTypes = FrozenSet.ToFrozenSet(new[]
        {
            EWorkshopFileType.Community,
            EWorkshopFileType.Art,
            EWorkshopFileType.Screenshot,
            EWorkshopFileType.Merch,
            EWorkshopFileType.IntegratedGuide,
            EWorkshopFileType.ControllerBinding,
        });

        private sealed class DepotDownloadInfo(
            uint depotid, uint appId, ulong manifestId, string branch,
            string installDir, byte[] depotKey)
        {
            public uint DepotId { get; } = depotid;
            public uint AppId { get; } = appId;
            public ulong ManifestId { get; } = manifestId;
            public string Branch { get; } = branch;
            public string InstallDir { get; } = installDir;
            public byte[] DepotKey { get; } = depotKey;
        }

        static bool CreateDirectories(uint depotId, uint depotVersion, out string installDir)
        {
            installDir = null;
            try
            {
                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

                    var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
                    Directory.CreateDirectory(depotPath);

                    installDir = Path.Combine(depotPath, depotVersion.ToString());
                    Directory.CreateDirectory(installDir);

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
                else
                {
                    Directory.CreateDirectory(Config.InstallDirectory);

                    installDir = Config.InstallDirectory;

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        static HashSet<string> ExtractAllParentDirectories(HashSet<string> filePaths)
        {
            var parentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (filePaths == null)
                return parentDirs;

            foreach (var filePath in filePaths)
            {
                var normalized = NormalizeManifestPath(filePath);
                if (string.IsNullOrEmpty(normalized))
                    continue;

                var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 1)
                    continue;

                for (int i = parts.Length - 1; i >= 1; i--)
                {
                    var parent = string.Join('/', parts, 0, i);
                    parentDirs.Add(parent);
                }
            }

            return parentDirs;
        }

        static bool TestIsFileIncluded(string filename)
        {
            if (!Config.UsingFileList)
                return true;

            filename = filename.Replace('\\', '/');

            if (Config.FilesToDownload.Contains(filename))
            {
                return true;
            }

            foreach (var rgx in Config.FilesToDownloadRegex)
            {
                if (rgx.Match(filename).Success)
                    return true;
            }

            return false;
        }

        static string NormalizeManifestPath(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }

        static bool IsPathInConfigDirectory(string path, string installDir)
        {
            var relative = Path.GetRelativePath(installDir, path).Replace('\\', '/').TrimEnd('/');
            return string.Equals(relative, CONFIG_DIR, StringComparison.OrdinalIgnoreCase)
                   || relative.StartsWith(CONFIG_DIR + "/", StringComparison.OrdinalIgnoreCase);
        }

        static void RemoveOrphanFiles(string installDir, HashSet<string> allowedRelativePaths)
        {
            if (allowedRelativePaths == null || !Config.DeleteOrphanFiles)
                return;

            var allowed = new HashSet<string>(allowedRelativePaths.Select(NormalizeManifestPath), StringComparer.OrdinalIgnoreCase);
            var deletedFiles = new List<string>();

            foreach (var filePath in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (IsPathInConfigDirectory(filePath, installDir))
                    continue;

                var relativePath = NormalizeManifestPath(Path.GetRelativePath(installDir, filePath));

                if (allowed.Contains(relativePath))
                    continue;

                try
                {
                    File.Delete(filePath);
                    deletedFiles.Add(relativePath);
                }
                catch (Exception ex)
                {
                    ProgressDisplay.WriteLine("Warning: Failed to delete orphan file {0}: {1}", filePath, ex.Message);
                }
            }

            if (deletedFiles.Count > 0)
            {
                ProgressDisplay.WriteLine("\x1B[91m  Deleted {0} orphan file(s):\x1B[0m", deletedFiles.Count);
                foreach (var deletedFile in deletedFiles)
                {
                    ProgressDisplay.WriteLine("\x1B[91m    - {0}\x1B[0m", deletedFile);
                }
            }
        }

        static void RemoveEmptyDirectories(string installDir, HashSet<string> allowedDirectories)
        {
            if (!Directory.Exists(installDir))
                return;

            var deletedDirs = new List<string>();

            foreach (var dirPath in Directory.EnumerateDirectories(installDir, "*", SearchOption.AllDirectories)
                                               .OrderByDescending(d => d.Length))
            {
                if (IsPathInConfigDirectory(dirPath, installDir))
                    continue;

                if (!Directory.Exists(dirPath))
                    continue;

                var relativeDirPath = NormalizeManifestPath(Path.GetRelativePath(installDir, dirPath));
                if (allowedDirectories != null && allowedDirectories.Contains(relativeDirPath))
                    continue;

                if (!Directory.EnumerateFileSystemEntries(dirPath).Any())
                {
                    try
                    {
                        Directory.Delete(dirPath);
                        deletedDirs.Add(relativeDirPath);
                    }
                    catch (Exception ex)
                    {
                        ProgressDisplay.WriteLine("Warning: Failed to delete empty folder {0}: {1}", dirPath, ex.Message);
                    }
                }
            }

            if (deletedDirs.Count > 0)
            {
                ProgressDisplay.WriteLine("\x1B[91m  Deleted {0} empty folder(s):\x1B[0m", deletedDirs.Count);
                foreach (var deletedDir in deletedDirs)
                {
                    ProgressDisplay.WriteLine("\x1B[91m    - {0}\x1B[0m", deletedDir);
                }
            }
        }

        internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
            {
                return null;
            }

            var appinfo = app.KeyValues;
            var section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };
            var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber(uint appId, string branch)
        {
            if (appId == INVALID_APP_ID)
                return 0;

            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null) return 0;
            var branches = depots["branches"];
            var node = branches[branch];

            if (node == KeyValue.Invalid)
                return 0;

            var buildid = node["buildid"];

            if (buildid == KeyValue.Invalid)
                return 0;

            return uint.Parse(buildid.Value);
        }

        static uint GetSteam3DepotProxyAppId(uint depotId, uint appId)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null) return INVALID_APP_ID;
            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_APP_ID;

            if (depotChild["depotfromapp"] == KeyValue.Invalid)
                return INVALID_APP_ID;

            return depotChild["depotfromapp"].AsUnsignedInteger();
        }

        static async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    ProgressDisplay.WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return INVALID_MANIFEST_ID;
                }

                await steam3.RequestAppInfo(otherAppId);

                return await GetSteam3DepotManifest(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];

            if (manifests.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            var node = manifests[branch]["gid"];

            if (node.Value != null)
                return ulong.Parse(node.Value);

            if (string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                return INVALID_MANIFEST_ID;

            if (string.IsNullOrEmpty(Config.BetaPassword))
            {
                ProgressDisplay.WriteLine($"Branch {branch} for depot {depotId} was not found, either it does not exist or it has a password.");
                return INVALID_MANIFEST_ID;
            }

            if (!steam3.AppBetaPasswords.ContainsKey(branch))
            {
                await steam3.CheckAppBetaPassword(appId, Config.BetaPassword);

                if (!steam3.AppBetaPasswords.ContainsKey(branch))
                {
                    ProgressDisplay.WriteLine($"Error: Password was invalid for branch {branch} (or the branch does not exist)");
                    return INVALID_MANIFEST_ID;
                }
            }

            var privateDepotSection = await steam3.GetPrivateBetaDepotSection(appId, branch);

            depotChild = privateDepotSection[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            manifests = depotChild["manifests"];

            if (manifests.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            node = manifests[branch]["gid"];

            if (node.Value == null)
                return INVALID_MANIFEST_ID;

            return ulong.Parse(node.Value);
        }

        static string GetAppName(uint appId)
        {
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info == null)
                return string.Empty;

            return info["name"].AsString();
        }

        public static bool InitializeSteam3(string username, string password)
        {
            string loginToken = null;

            if (username != null && Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginToken == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    AccessToken = loginToken,
                    LoginID = Config.LoginID ?? 0x534B32,
                }
            );

            if (!steam3.WaitForCredentials())
            {
                ProgressDisplay.WriteLine("Unable to get steam3 credentials.");
                return false;
            }

            Task.Run(steam3.TickCallbacks);

            return true;
        }

        public static void ShutdownSteam3()
        {
            if (steam3 == null)
                return;

            steam3.Disconnect();
        }

        private static async Task ProcessPublishedFileAsync(uint appId, ulong publishedFileId, List<ValueTuple<string, string>> fileUrls, List<ulong> contentFileIds)
        {
            var details = await steam3.GetPublishedFileDetails(appId, publishedFileId);
            var fileType = (EWorkshopFileType)details.file_type;

            if (fileType == EWorkshopFileType.Collection)
            {
                foreach (var child in details.children)
                {
                    await ProcessPublishedFileAsync(appId, child.publishedfileid, fileUrls, contentFileIds);
                }
            }
            else if (SupportedWorkshopFileTypes.Contains(fileType))
            {
                if (!string.IsNullOrEmpty(details?.file_url))
                {
                    fileUrls.Add((details.filename, details.file_url));
                }
                else if (details?.hcontent_file > 0)
                {
                    contentFileIds.Add(details.hcontent_file);
                }
                else
                {
                    ProgressDisplay.WriteLine("Unable to locate manifest ID for published file {0}", publishedFileId);
                }
            }
            else
            {
                ProgressDisplay.WriteLine("Published file {0} has unsupported file type {1}. Skipping file", publishedFileId, fileType);
            }
        }

        public static async Task DownloadPubfileAsync(uint appId, ulong publishedFileId)
        {
            List<ValueTuple<string, string>> fileUrls = new();
            List<ulong> contentFileIds = new();

            await ProcessPublishedFileAsync(appId, publishedFileId, fileUrls, contentFileIds);

            foreach (var item in fileUrls)
            {
                await DownloadWebFile(appId, item.Item1, item.Item2);
            }

            if (contentFileIds.Count > 0)
            {
                var depotManifestIds = contentFileIds.Select(id => (appId, id)).ToList();
                await DownloadAppAsync(appId, depotManifestIds, DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        public static async Task DownloadUGCAsync(uint appId, ulong ugcId)
        {
            SteamCloud.UGCDetailsCallback details = null;

            if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser)
            {
                details = await steam3.GetUGCDetails(ugcId);
            }
            else
            {
                ProgressDisplay.WriteLine($"Unable to query UGC details for {ugcId} from an anonymous account");
            }

            if (!string.IsNullOrEmpty(details?.URL))
            {
                await DownloadWebFile(appId, details.FileName, details.URL);
            }
            else
            {
                await DownloadAppAsync(appId, [(appId, ugcId)], DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        private static async Task DownloadWebFile(uint appId, string fileName, string url)
        {
            if (!CreateDirectories(appId, 0, out var installDir))
            {
                ProgressDisplay.WriteLine("Error: Unable to create install directories!");
                return;
            }

            var stagingDir = Path.Combine(installDir, STAGING_DIR);
            var fileStagingPath = Path.Combine(stagingDir, fileName);
            var fileFinalPath = Path.Combine(installDir, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath)!);

            using (var file = File.OpenWrite(fileStagingPath))
            using (var client = HttpClientFactory.CreateHttpClient())
            {
                var responseStream = await client.GetStreamAsync(url);
                await responseStream.CopyToAsync(file);
            }

            if (File.Exists(fileFinalPath))
            {
                File.Delete(fileFinalPath);
            }

            File.Move(fileStagingPath, fileFinalPath);
        }

        public static async Task DownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc)
        {
            cdnPool = new CDNClientPool(steam3, appId);

            var configPath = Config.InstallDirectory;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                configPath = DEFAULT_DOWNLOAD_DIR;
            }

            Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
            DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, "depot.config"));

            await steam3?.RequestAppInfo(appId);

            var hasSpecificDepots = depotManifestIds.Count > 0;
            var depotIdsFound = new List<uint>();
            var depotIdsExpected = depotManifestIds.Select(x => x.depotId).ToList();
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

            if (isUgc)
            {
                var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
                if (workshopDepot != 0 && !depotIdsExpected.Contains(workshopDepot))
                {
                    depotIdsExpected.Add(workshopDepot);
                    depotManifestIds = depotManifestIds.Select(pair => (workshopDepot, pair.manifestId)).ToList();
                }

                depotIdsFound.AddRange(depotIdsExpected);
            }
            else
            {
                if (depots != null)
                {
                    foreach (var depotSection in depots.Children)
                    {
                        var id = INVALID_DEPOT_ID;
                        if (depotSection.Children.Count == 0)
                            continue;

                        if (!uint.TryParse(depotSection.Name, out id))
                            continue;

                        if (hasSpecificDepots && !depotIdsExpected.Contains(id))
                            continue;

                        if (!hasSpecificDepots)
                        {
                            var depotConfig = depotSection["config"];
                            if (depotConfig != KeyValue.Invalid)
                            {
                                if (!Config.DownloadAllPlatforms &&
                                    depotConfig["oslist"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                                {
                                    var oslist = depotConfig["oslist"].Value.Split(',');
                                    if (Array.IndexOf(oslist, os ?? Util.GetSteamOS()) == -1)
                                        continue;
                                }

                                if (!Config.DownloadAllArchs &&
                                    depotConfig["osarch"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
                                {
                                    var depotArch = depotConfig["osarch"].Value;
                                    if (depotArch != (arch ?? Util.GetSteamArch()))
                                        continue;
                                }

                                if (!Config.DownloadAllLanguages &&
                                    depotConfig["language"] != KeyValue.Invalid &&
                                    !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
                                {
                                    var depotLang = depotConfig["language"].Value;
                                    if (depotLang != (language ?? "english"))
                                        continue;
                                }

                                if (!lv &&
                                    depotConfig["lowviolence"] != KeyValue.Invalid &&
                                    depotConfig["lowviolence"].AsBoolean())
                                    continue;
                            }
                        }

                        depotIdsFound.Add(id);

                        if (!hasSpecificDepots)
                            depotManifestIds.Add((id, INVALID_MANIFEST_ID));
                    }
                }

                if (depotManifestIds.Count == 0 && !hasSpecificDepots)
                {
                    throw new ContentDownloaderException(string.Format("Couldn't find any depots to download for app {0}", appId));
                }
            }

            var infos = new List<DepotDownloadInfo>();

            foreach (var (depotId, manifestId) in depotManifestIds)
            {
                var info = await GetDepotInfo(depotId, appId, manifestId, branch);
                if (info != null)
                {
                    infos.Add(info);
                }
            }

            try
            {
                await DownloadSteam3Async(infos).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ProgressDisplay.WriteLine("App {0} was not completely downloaded.", appId);
                throw;
            }
        }

        static async Task<DepotDownloadInfo> GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (steam3 != null && appId != INVALID_APP_ID)
            {
                await steam3.RequestAppInfo(appId);
            }

            if (manifestId == INVALID_MANIFEST_ID)
            {
                manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
                if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                {
                    ProgressDisplay.WriteLine("Warning: Depot {0} does not have branch named \"{1}\". Trying {2} branch.", depotId, branch, DEFAULT_BRANCH);
                    branch = DEFAULT_BRANCH;
                    manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
                }

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    ProgressDisplay.WriteLine("Depot {0} missing public subsection or manifest section.", depotId);
                    return null;
                }
            }

            byte[] depotKey = null;
            if (DepotKeyStore.ContainsKey(depotId))
            {
                depotKey = DepotKeyStore.Get(depotId);
                steam3.DepotKeys.Add(depotId, depotKey);
            }
            else
            {
                await steam3.RequestDepotKey(depotId, appId);
            }
            if (!steam3.DepotKeys.TryGetValue(depotId, out depotKey))
            {
                ProgressDisplay.WriteLine("No valid depot key for {0}, unable to download.", depotId);
                return null;
            }

            var uVersion = GetSteam3AppBuildNumber(appId, branch);

            if (!CreateDirectories(depotId, uVersion, out var installDir))
            {
                ProgressDisplay.WriteLine("Error: Unable to create install directories!");
                return null;
            }

            var containingAppId = appId;
            var proxyAppId = GetSteam3DepotProxyAppId(depotId, appId);
            if (proxyAppId != INVALID_APP_ID)
            {
                var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (common == null || !common["FreeToDownload"].AsBoolean())
                {
                    containingAppId = proxyAppId;
                }
            }

            return new DepotDownloadInfo(depotId, containingAppId, manifestId, branch, installDir, depotKey);
        }

        private class ChunkMatch(DepotManifest.ChunkData oldChunk, DepotManifest.ChunkData newChunk)
        {
            public DepotManifest.ChunkData OldChunk { get; } = oldChunk;
            public DepotManifest.ChunkData NewChunk { get; } = newChunk;
        }

        private class DepotFilesData
        {
            public DepotDownloadInfo depotDownloadInfo;
            public DepotDownloadCounter depotCounter;
            public string stagingDir;
            public DepotManifest manifest;
            public DepotManifest previousManifest;
            public List<DepotManifest.FileData> filteredFiles;
            public HashSet<string> allFileNames;
        }

        private class FileStreamData
        {
            public FileStream fileStream;
            public SemaphoreSlim fileLock;
            public int chunksToDownload;
        }

        private class GlobalDownloadCounter
        {
            public ulong completeDownloadSize;
            public ulong totalBytesCompressed;

            // Total work to do (validation + download) and how much is done.
            // Each byte of file work counts once, whether it's validation or download.
            public ulong totalWork;
            public ulong workDone;

            // Total compressed bytes that need to be downloaded (actual update size).
            public long totalUpdateSizeCompressed;

            // Bytes downloaded so far (tracked separately from workDone for the download-phase progress bar).
            public ulong downloadWorkDone;

            // Number of files that still need to be scanned/validated.
            public int FilesToScan;
            public volatile bool HasDownloads;

            public bool IsScanning => FilesToScan > 0;

            public ulong GetTotalUpdateSize() => (ulong)Interlocked.Read(ref totalUpdateSizeCompressed);
        }

        private class DepotDownloadCounter
        {
            public ulong completeDownloadSize;
            public ulong sizeDownloaded;
            public ulong depotBytesCompressed;
            public ulong depotBytesUncompressed;
        }

        private static async Task DownloadSteam3Async(List<DepotDownloadInfo> depots)
        {
            Ansi.Progress(Ansi.ProgressState.Indeterminate);

            await cdnPool.UpdateServerList();

            var cts = new CancellationTokenSource();
            var downloadCounter = new GlobalDownloadCounter();
            var depotsToDownload = new List<DepotFilesData>(depots.Count);
            var allFileNamesAllDepots = new HashSet<string>();

            foreach (var depot in depots)
            {
                var depotFileData = await ProcessDepotManifestAndFiles(cts, depot, downloadCounter);

                if (depotFileData != null)
                {
                    depotsToDownload.Add(depotFileData);
                    allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);
                }

                cts.Token.ThrowIfCancellationRequested();
            }

            if (!string.IsNullOrWhiteSpace(Config.InstallDirectory) && depotsToDownload.Count > 0)
            {
                var claimedFileNames = new HashSet<string>();

                for (var i = depotsToDownload.Count - 1; i >= 0; i--)
                {
                    depotsToDownload[i].filteredFiles.RemoveAll(file => claimedFileNames.Contains(file.FileName));
                    claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
                }
            }

            foreach (var depotFileData in depotsToDownload)
            {
                var allowedRelativePaths = string.IsNullOrWhiteSpace(Config.InstallDirectory)
                    ? depotFileData.allFileNames
                    : allFileNamesAllDepots;

                RemoveOrphanFiles(depotFileData.depotDownloadInfo.InstallDir, allowedRelativePaths);
            }

            foreach (var depotFileData in depotsToDownload)
            {
                if (depotFileData.previousManifest == null)
                    continue;

                var previousFilteredFiles = depotFileData.previousManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet();

                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    previousFilteredFiles.ExceptWith(depotFileData.allFileNames);
                }
                else
                {
                    previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
                }

                var removedFiles = new List<string>();

                foreach (var existingFileName in previousFilteredFiles)
                {
                    var fileFinalPath = Path.Combine(depotFileData.depotDownloadInfo.InstallDir, existingFileName);

                    if (!File.Exists(fileFinalPath))
                        continue;

                    try
                    {
                        File.Delete(fileFinalPath);
                        removedFiles.Add(existingFileName);
                    }
                    catch (Exception ex)
                    {
                        ProgressDisplay.WriteLine("Warning: Failed to delete {0}: {1}", fileFinalPath, ex.Message);
                    }
                }

                if (removedFiles.Count > 0)
                {
                    ProgressDisplay.WriteLine("\x1B[91m  Removed {0} file(s) from old manifest:\x1B[0m", removedFiles.Count);
                    foreach (var removedFile in removedFiles)
                    {
                        ProgressDisplay.WriteLine("\x1B[91m    - {0}\x1B[0m", removedFile);
                    }
                }
            }

            ProgressDisplay.Start(() =>
            {
                lock (downloadCounter)
                {
                    if (downloadCounter.IsScanning)
                    {
                        // Validation phase: show bytes validated out of total file bytes
                        return (downloadCounter.workDone, downloadCounter.totalWork);
                    }
                    else if (downloadCounter.HasDownloads)
                    {
                        // Download phase: show bytes downloaded out of total update size
                        var totalUpdate = downloadCounter.GetTotalUpdateSize();
                        return (downloadCounter.downloadWorkDone, totalUpdate);
                    }
                    else
                    {
                        // Nothing to download
                        return (0UL, 0UL);
                    }
                }
            }, () =>
            {
                lock (downloadCounter)
                {
                    return downloadCounter.IsScanning;
                }
            }, () =>
            {
                lock (downloadCounter)
                {
                    return downloadCounter.HasDownloads;
                }
            }, () => downloadCounter.GetTotalUpdateSize());

            try
            {
                foreach (var depotFileData in depotsToDownload)
                {
                    await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFileData, allFileNamesAllDepots);
                }
            }
            finally
            {
                ProgressDisplay.Stop();
            }

            foreach (var depotFileData in depotsToDownload)
            {
                var allowedRelativePaths = string.IsNullOrWhiteSpace(Config.InstallDirectory)
                    ? depotFileData.allFileNames
                    : allFileNamesAllDepots;

                var allowedDirectories = ExtractAllParentDirectories(allowedRelativePaths);
                RemoveEmptyDirectories(depotFileData.depotDownloadInfo.InstallDir, allowedDirectories);
            }

            Ansi.Progress(Ansi.ProgressState.Hidden);

            // Single clean summary matching the update size shown in the progress bar
            ProgressDisplay.WriteLine("Downloaded update: {0} from {1} depot(s)",
                ProgressDisplay.FormatBytes(downloadCounter.GetTotalUpdateSize()),
                depots.Count);
        }

        private static async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
        {
            var depotCounter = new DepotDownloadCounter();

            ProgressDisplay.WriteLine("\x1B[1mDepot {0}\x1B[0m", depot.DepotId);

            DepotManifest oldManifest = null;
            DepotManifest newManifest = null;
            var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

            var lastManifestId = INVALID_MANIFEST_ID;
            DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

            // In case we have an early exit, this will force equiv of verifyall next run.
            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
            DepotConfigStore.Save();

            if (lastManifestId != INVALID_MANIFEST_ID)
            {
                var badHashWarning = (lastManifestId != depot.ManifestId);
                oldManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, lastManifestId, badHashWarning);
            }

            if (Config.UseManifestFile)
            {
                try
                {
                    newManifest = DepotManifest.LoadFromFile(Config.ManifestFile);
                    if (newManifest.FilenamesEncrypted)
                    {
                        if (!newManifest.DecryptFilenames(depot.DepotKey))
                        {
                            ProgressDisplay.WriteLine("Failed to decrypt filenames in manifest file.");
                            return null;
                        }
                    }

                    Util.SaveManifestToFile(configDir, newManifest);
                }
                catch (Exception e)
                {
                    ProgressDisplay.WriteLine("Failed to load manifest file '{0}': {1}", Config.ManifestFile, e.Message);
                    return null;
                }
            }

            if (newManifest != null)
            {
                ProgressDisplay.WriteLine("  Using specified manifest file.");
            }
            else if (lastManifestId == depot.ManifestId && oldManifest != null)
            {
                newManifest = oldManifest;
                ProgressDisplay.WriteLine("  Cached manifest {0}.", depot.ManifestId);
            }
            else
            {
                newManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, depot.ManifestId, true);

                if (newManifest != null)
                {
                    ProgressDisplay.WriteLine("  Cached manifest {0}.", depot.ManifestId);
                }
                else
                {
                    ProgressDisplay.WriteLine("  Downloading manifest...");

                    ulong manifestRequestCode = 0;
                    var manifestRequestCodeExpiration = DateTime.MinValue;

                    do
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        Server connection = null;

                        try
                        {
                            connection = cdnPool.GetConnection();

                            string cdnToken = null;
                            if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                            {
                                var result = await authTokenCallbackPromise.Task;
                                cdnToken = result.Token;
                            }

                            var now = DateTime.Now;

                            if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                            {
                                manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                    depot.DepotId,
                                    depot.AppId,
                                    depot.ManifestId,
                                    depot.Branch);
                                manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                                if (manifestRequestCode == 0)
                                {
                                    cts.Cancel();
                                }
                            }

                            DebugLog.WriteLine("ContentDownloader",
                                "Downloading manifest {0} from {1} with {2}",
                                depot.ManifestId,
                                connection,
                                cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                            newManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                                depot.DepotId,
                                depot.ManifestId,
                                manifestRequestCode,
                                connection,
                                depot.DepotKey,
                                cdnPool.ProxyServer,
                                cdnToken).ConfigureAwait(false);

                            cdnPool.ReturnConnection(connection);
                        }
                        catch (TaskCanceledException)
                        {
                            ProgressDisplay.WriteLine("Connection timeout downloading depot manifest {0} {1}. Retrying.", depot.DepotId, depot.ManifestId);
                        }
                        catch (SteamKitWebRequestException e)
                        {
                            if (e.StatusCode == HttpStatusCode.Forbidden && !steam3.CDNAuthTokens.ContainsKey((depot.DepotId, connection.Host)))
                            {
                                await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);
                                cdnPool.ReturnConnection(connection);
                                continue;
                            }

                            cdnPool.ReturnBrokenConnection(connection);

                            if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                            {
                                ProgressDisplay.WriteLine("Encountered {2} for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId, (int)e.StatusCode);
                                break;
                            }

                            if (e.StatusCode == HttpStatusCode.NotFound)
                            {
                                ProgressDisplay.WriteLine("Encountered 404 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
                                break;
                            }

                            ProgressDisplay.WriteLine("Encountered error downloading depot manifest {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.StatusCode);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            cdnPool.ReturnBrokenConnection(connection);
                            ProgressDisplay.WriteLine("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
                        }
                    } while (newManifest == null);

                    if (newManifest == null)
                    {
                        ProgressDisplay.WriteLine("Unable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
                        cts.Cancel();
                    }

                    // Throw the cancellation exception if requested so that this task is marked failed
                    cts.Token.ThrowIfCancellationRequested();

                    Util.SaveManifestToFile(configDir, newManifest);
                }
            }

            ProgressDisplay.WriteLine("  Manifest {0}  ({1})", depot.ManifestId, newManifest.CreationTime);

            if (Config.DownloadManifestOnly)
            {
                DumpManifestToTextFile(depot, newManifest);
                return null;
            }

            var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

            var filesAfterExclusions = newManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
            var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

            if (oldManifest != null)
            {
                ProgressDisplay.WriteLine("  Delta patching against manifest {0}.", lastManifestId);
            }

            filesAfterExclusions.ForEach(file =>
            {
                allFileNames.Add(file.FileName);

                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                var fileStagingPath = Path.Combine(stagingDir, file.FileName);

                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    Directory.CreateDirectory(fileFinalPath);
                    Directory.CreateDirectory(fileStagingPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath)!);

                    downloadCounter.completeDownloadSize += file.TotalSize;
                    depotCounter.completeDownloadSize += file.TotalSize;
                }
            });

            return new DepotFilesData
            {
                depotDownloadInfo = depot,
                depotCounter = depotCounter,
                stagingDir = stagingDir,
                manifest = newManifest,
                previousManifest = oldManifest,
                filteredFiles = filesAfterExclusions,
                allFileNames = allFileNames
            };
        }

        private static async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
        {
            var depot = depotFilesData.depotDownloadInfo;
            var depotCounter = depotFilesData.depotCounter;

            var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
            var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, DepotManifest.FileData fileData, DepotManifest.ChunkData chunk)>();

            // Seed totalWork upfront, subtracting files that don't exist on disk (new files)
            // since they require no validation work — they go straight to download.
            foreach (var f in files)
            {
                var filePath = Path.Combine(depot.InstallDir, f.FileName);
                if (File.Exists(filePath))
                {
                    downloadCounter.totalWork += f.TotalSize;
                }
            }

            downloadCounter.FilesToScan = files.Length;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Config.MaxDownloads,
                CancellationToken = cts.Token
            };

            await Parallel.ForEachAsync(files, parallelOptions, async (file, cancellationToken) =>
            {
                await Task.Yield();
                DownloadSteam3AsyncDepotFile(cts, downloadCounter, depotFilesData, file, networkChunkQueue);
                Interlocked.Decrement(ref downloadCounter.FilesToScan);
            });

            await Parallel.ForEachAsync(networkChunkQueue, parallelOptions, async (q, cancellationToken) =>
            {
                await DownloadSteam3AsyncDepotFileChunk(
                    cts, downloadCounter, depotFilesData,
                    q.fileData, q.fileStreamData, q.chunk
                );
            });

            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
            DepotConfigStore.Save();
        }

        private static void DownloadSteam3AsyncDepotFile(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            DepotManifest.FileData file,
            ConcurrentQueue<(FileStreamData, DepotManifest.FileData, DepotManifest.ChunkData)> networkChunkQueue)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var stagingDir = depotFilesData.stagingDir;
            var depotDownloadCounter = depotFilesData.depotCounter;
            var oldProtoManifest = depotFilesData.previousManifest;
            DepotManifest.FileData oldManifestFile = null;
            if (oldProtoManifest != null)
            {
                oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
            }

            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            var fileStagingPath = Path.Combine(stagingDir, file.FileName);

            if (File.Exists(fileStagingPath))
            {
                File.Delete(fileStagingPath);
            }

            List<DepotManifest.ChunkData> neededChunks;
            var fi = new FileInfo(fileFinalPath);
            var fileDidExist = fi.Exists;
            if (!fileDidExist)
            {
                // New file — pre-allocate and download all chunks
                using var fs = File.Create(fileFinalPath);
                try
                {
                    fs.SetLength((long)file.TotalSize);
                }
                catch (IOException ex)
                {
                    throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                }

                neededChunks = new List<DepotManifest.ChunkData>(file.Chunks);

                ProgressDisplay.WriteLine("\x1B[90m  + {0}  ({1})\x1B[0m",
                    file.FileName, ProgressDisplay.FormatBytes(file.TotalSize));
            }
            else
            {
                // totalWork was already seeded upfront for all files

                if (oldManifestFile != null)
                {
                    neededChunks = [];

                    var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
                    if (hashMatches && !Config.VerifyAll)
                    {
                        // File hash matches — entire file is validated without chunk scanning
                        lock (downloadCounter)
                        {
                            downloadCounter.workDone += file.TotalSize;
                        }
                    }
                    if (Config.VerifyAll || !hashMatches)
                    {
                        var matchingChunks = new List<ChunkMatch>();
                        var matchedCount = 0;

                        foreach (var chunk in file.Chunks)
                        {
                            var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                            if (oldChunk != null)
                            {
                                matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
                            }
                            else
                            {
                                neededChunks.Add(chunk);

                                // Chunk has no old match — it will be downloaded, not validated.
                                // Subtract from totalWork so validation bar reflects actual work.
                                lock (downloadCounter)
                                {
                                    downloadCounter.totalWork -= chunk.UncompressedLength;
                                }
                            }
                        }

                        var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

                        var copyChunks = new List<ChunkMatch>();

                        using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
                        {
                            foreach (var match in orderedChunks)
                            {
                                fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                var adler = Util.AdlerHash(fsOld, (int)match.OldChunk.UncompressedLength);
                                if (!adler.SequenceEqual(BitConverter.GetBytes(match.OldChunk.Checksum)))
                                {
                                    neededChunks.Add(match.NewChunk);

                                    // Chunk failed validation — it will be downloaded, not counted as validated.
                                    // Subtract from totalWork so validation bar reflects actual work.
                                    lock (downloadCounter)
                                    {
                                        downloadCounter.totalWork -= match.NewChunk.UncompressedLength;
                                    }
                                }
                                else
                                {
                                    copyChunks.Add(match);
                                    matchedCount++;

                                    // Count validated (copied) chunks
                                    lock (downloadCounter)
                                    {
                                        downloadCounter.workDone += match.NewChunk.UncompressedLength;
                                    }
                                }
                            }
                        }

                        // Show matched chunks info in dim gray
                        if (matchedCount > 0 || copyChunks.Count > 0 || neededChunks.Count > 0)
                        {
                            ProgressDisplay.WriteLine("\x1B[90m  ~ {0}  matched: {1}, copied: {2}, needed: {3}\x1B[0m",
                                file.FileName, matchedCount, copyChunks.Count, neededChunks.Count);
                        }

                        if (!hashMatches || neededChunks.Count > 0)
                        {
                            File.Move(fileFinalPath, fileStagingPath);

                            using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                            {
                                using var fs = File.Open(fileFinalPath, FileMode.Create);
                                try
                                {
                                    fs.SetLength((long)file.TotalSize);
                                }
                                catch (IOException ex)
                                {
                                    throw new ContentDownloaderException(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
                                }

                                foreach (var match in copyChunks)
                                {
                                    fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                    var tmp = new byte[match.OldChunk.UncompressedLength];
                                    fsOld.ReadExactly(tmp);

                                    fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                    fs.Write(tmp, 0, tmp.Length);
                                }
                            }

                            File.Delete(fileStagingPath);
                        }
                    }
                }
                else
                {
                    // No old manifest — validate each chunk individually so
                    // progress updates smoothly.
                    using var fs = File.Open(fileFinalPath, FileMode.Open);
                    if ((ulong)fi.Length != file.TotalSize)
                    {
                        try
                        {
                            fs.SetLength((long)file.TotalSize);
                        }
                        catch (IOException ex)
                        {
                            throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                        }
                    }

                    neededChunks = [];
                    var validatedCount = 0;
                    var orderedChunks = file.Chunks.OrderBy(x => x.Offset).ToList();
                    foreach (var chunk in orderedChunks)
                    {
                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        var adler = Util.AdlerHash(fs, (int)chunk.UncompressedLength);
                        if (adler.SequenceEqual(BitConverter.GetBytes(chunk.Checksum)))
                        {
                            validatedCount++;

                            // Count validated chunks
                            lock (downloadCounter)
                            {
                                downloadCounter.workDone += chunk.UncompressedLength;
                            }
                        }
                        else
                        {
                            neededChunks.Add(chunk);

                            // Chunk failed validation — it will be downloaded, not counted as validated.
                            // Subtract from totalWork so validation bar reflects actual work.
                            lock (downloadCounter)
                            {
                                downloadCounter.totalWork -= chunk.UncompressedLength;
                            }
                        }
                    }

                    // Show validation result in dim gray
                    ProgressDisplay.WriteLine("\x1B[90m  ~ {0}  validated: {1}/{2} chunks, needed: {3}\x1B[0m",
                        file.FileName, validatedCount, file.Chunks.Count, neededChunks.Count);
                }

                if (neededChunks.Count == 0)
                {
                    lock (depotDownloadCounter)
                    {
                        depotDownloadCounter.sizeDownloaded += file.TotalSize;
                    }

                    lock (downloadCounter)
                    {
                        downloadCounter.completeDownloadSize -= file.TotalSize;
                    }

                    // File is up to date — show in dim green
                    ProgressDisplay.WriteLine("\x1B[32m  ✓ {0}\x1B[0m", file.FileName);

                    return;
                }

                var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
                lock (depotDownloadCounter)
                {
                    depotDownloadCounter.sizeDownloaded += sizeOnDisk;
                }

                lock (downloadCounter)
                {
                    downloadCounter.completeDownloadSize -= sizeOnDisk;
                }
            }

            var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
            if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, true);
            }
            else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, false);
            }

            var fileStreamData = new FileStreamData
            {
                fileStream = null,
                fileLock = new SemaphoreSlim(1),
                chunksToDownload = neededChunks.Count
            };

            if (neededChunks.Count > 0)
            {
                downloadCounter.HasDownloads = true;
                var compressedSize = (long)neededChunks.Sum(x => (long)x.CompressedLength);
                Interlocked.Add(ref downloadCounter.totalUpdateSizeCompressed, compressedSize);
            }

            foreach (var chunk in neededChunks)
            {
                networkChunkQueue.Enqueue((fileStreamData, file, chunk));
            }
        }

        private static async Task DownloadSteam3AsyncDepotFileChunk(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            DepotManifest.FileData file,
            FileStreamData fileStreamData,
            DepotManifest.ChunkData chunk)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var depotDownloadCounter = depotFilesData.depotCounter;

            var chunkID = Convert.ToHexString(chunk.ChunkID).ToLowerInvariant();

            var written = 0;
            var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

            try
            {
                do
                {
                    cts.Token.ThrowIfCancellationRequested();

                    Server connection = null;

                    try
                    {
                        connection = cdnPool.GetConnection();

                        string cdnToken = null;
                        if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        DebugLog.WriteLine("ContentDownloader", "Downloading chunk {0} from {1} with {2}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                        written = await cdnPool.CDNClient.DownloadDepotChunkAsync(
                            depot.DepotId,
                            chunk,
                            connection,
                            chunkBuffer,
                            depot.DepotKey,
                            cdnPool.ProxyServer,
                            cdnToken).ConfigureAwait(false);

                        cdnPool.ReturnConnection(connection);

                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        ProgressDisplay.WriteLine("Connection timeout downloading chunk {0}", chunkID);
                        cdnPool.ReturnBrokenConnection(connection);
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        if (e.StatusCode == HttpStatusCode.Forbidden &&
                            (!steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
                        {
                            await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);
                            cdnPool.ReturnConnection(connection);
                            continue;
                        }

                        cdnPool.ReturnBrokenConnection(connection);

                        if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                        {
                            ProgressDisplay.WriteLine("Encountered {1} for chunk {0}. Aborting.", chunkID, (int)e.StatusCode);
                            break;
                        }

                        ProgressDisplay.WriteLine("Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        cdnPool.ReturnBrokenConnection(connection);
                        ProgressDisplay.WriteLine("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
                    }
                } while (written == 0);

                if (written == 0)
                {
                    ProgressDisplay.WriteLine("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
                    cts.Cancel();
                }

                cts.Token.ThrowIfCancellationRequested();

                try
                {
                    await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

                    if (fileStreamData.fileStream == null)
                    {
                        var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                        fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);
                    }

                    fileStreamData.fileStream.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
                }
                finally
                {
                    fileStreamData.fileLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuffer);
            }

            var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
            if (remainingChunks == 0)
            {
                fileStreamData.fileStream?.Dispose();
                fileStreamData.fileLock.Dispose();
            }

            ulong sizeDownloaded = 0;
            lock (depotDownloadCounter)
            {
                sizeDownloaded = depotDownloadCounter.sizeDownloaded + (ulong)written;
                depotDownloadCounter.sizeDownloaded = sizeDownloaded;
                depotDownloadCounter.depotBytesCompressed += chunk.CompressedLength;
                depotDownloadCounter.depotBytesUncompressed += chunk.UncompressedLength;
            }

            lock (downloadCounter)
            {
                downloadCounter.totalBytesCompressed += chunk.CompressedLength;
                downloadCounter.workDone += chunk.UncompressedLength;
                downloadCounter.downloadWorkDone += chunk.CompressedLength;

                var totalUpdate = downloadCounter.GetTotalUpdateSize();
                if (totalUpdate > 0)
                {
                    Ansi.Progress(downloadCounter.downloadWorkDone, totalUpdate);
                }
            }
        }

        class ChunkIdComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.SequenceEqual(y);
            }

            public int GetHashCode(byte[] obj)
            {
                ArgumentNullException.ThrowIfNull(obj);
                return BitConverter.ToInt32(obj, 0);
            }
        }

        static void DumpManifestToTextFile(DepotDownloadInfo depot, DepotManifest manifest)
        {
            var txtManifest = Path.Combine(depot.InstallDir, $"manifest_{depot.DepotId}_{depot.ManifestId}.txt");
            using var sw = new StreamWriter(txtManifest);

            sw.WriteLine($"Content Manifest for Depot {depot.DepotId} ");
            sw.WriteLine();
            sw.WriteLine($"Manifest ID / date     : {depot.ManifestId} / {manifest.CreationTime} ");

            var uniqueChunks = new HashSet<byte[]>(new ChunkIdComparer());

            foreach (var file in manifest.Files)
            {
                foreach (var chunk in file.Chunks)
                {
                    uniqueChunks.Add(chunk.ChunkID);
                }
            }

            sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
            sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
            sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
            sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
            sw.WriteLine();
            sw.WriteLine();
            sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

            foreach (var file in manifest.Files)
            {
                var sha1Hash = Convert.ToHexString(file.FileHash).ToLower();
                sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
            }
        }
    }
}
