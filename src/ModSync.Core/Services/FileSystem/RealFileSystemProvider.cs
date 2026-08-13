// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ModSync.Core.FileSystemUtils;
using ModSync.Core.Utility;

using SharpCompress.Archives;
using SharpCompress.Readers;

namespace ModSync.Core.Services.FileSystem
{


    public class RealFileSystemProvider : IFileSystemProvider
    {
        public bool IsDryRun => false;

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite)
        {
            string directoryName = Path.GetDirectoryName(destinationPath);
            if (directoryName != null && !Directory.Exists(directoryName))
            {
                Directory.CreateDirectory(directoryName);
            }

            File.Copy(sourcePath, destinationPath, overwrite);
            return Task.CompletedTask;
        }

        public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite)
        {
            string directoryName = Path.GetDirectoryName(destinationPath);
            if (directoryName != null && !Directory.Exists(directoryName))
            {
                Directory.CreateDirectory(directoryName);
            }

            if (File.Exists(destinationPath) && overwrite)
            {
                File.Delete(destinationPath);
            }

            File.Move(sourcePath, destinationPath);
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string path)
        {
            File.Delete(path);
            return Task.CompletedTask;
        }

        public Task RenameFileAsync(string sourcePath, string newFileName, bool overwrite)
        {
            string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            string destinationPath = Path.Combine(directory, newFileName);

            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(sourcePath, destinationPath);
            return Task.CompletedTask;
        }

        public Task<string> ReadFileAsync(string path)
        {
            string content = File.ReadAllText(path);
            return Task.FromResult(content);
        }

        public Task WriteFileAsync(string path, string contents)
        {
            File.WriteAllText(path, contents);
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path)
        {
            _ = Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }

        public async Task<List<string>> ExtractArchiveAsync(string archivePath, string destinationPath)
        {
            var extractedFiles = new List<string>();
            int maxCount = MainConfig.UseMultiThreadedIO ? 16 : 1;

            using (var semaphore = new SemaphoreSlim(initialCount: 1, maxCount))
            {
                using (var cts = new CancellationTokenSource())
                {
                    try
                    {
                        await InnerExtractFileAsync(archivePath, destinationPath, extractedFiles, semaphore, cts.Token).ConfigureAwait(false);
                    }
                    catch (IndexOutOfRangeException ex)
                    {
                        await Logger.LogWarningAsync("Falling back to 7-Zip and restarting entire archive extraction due to the above error.").ConfigureAwait(false);
                        cts.Cancel();
                        throw new OperationCanceledException("Falling back to 7-Zip extraction", innerException: ex);
                    }
                    catch (OperationCanceledException ex)
                    {
                        await Logger.LogWarningAsync(ex.Message).ConfigureAwait(false);
                        throw;
                    }
                    catch (IOException ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        throw;
                    }
                }
            }

            return extractedFiles;

            async Task InnerExtractFileAsync(string sourcePath, string destPath, List<string> extracted, SemaphoreSlim sem, CancellationToken token)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await sem.WaitAsync(token).ConfigureAwait(false);

                try
                {
                    var archive = new FileInfo(sourcePath);
                    string sourceRelDirPath = MainConfig.SourcePath is null ? sourcePath : PathHelper.GetRelativePath(MainConfig.SourcePath.FullName, sourcePath);

                    await Logger.LogAsync($"Extracting archive '{sourcePath}'...").ConfigureAwait(false);

                    // Determine if destination was explicitly provided (different from archive's directory)
                    // When explicitly provided, extract directly to destination without archive name subfolder
                    // When using default (archive directory), add archive name subfolder to avoid conflicts
                    string archiveDirectory = Path.GetDirectoryName(archive.FullName) ?? string.Empty;
                    string normalizedDestPath = Path.GetFullPath(destPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string normalizedArchiveDir = Path.GetFullPath(archiveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    bool isExplicitDestination = !string.Equals(normalizedDestPath, normalizedArchiveDir, StringComparison.OrdinalIgnoreCase);

                    if (archive.Extension.Equals(value: ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ArchiveHelper.TryExtractSevenZipSfx(archive.FullName, destPath, extracted))
                        {
                            await Logger.LogAsync($"Successfully extracted 7z SFX archive via managed extraction: '{sourceRelDirPath}'").ConfigureAwait(false);
                            return;
                        }

                        if (ModSync.Core.Utility.UtilityHelper.GetOperatingSystem() != OSPlatform.Windows)
                        {
                            await Logger.LogAsync($"Managed SFX extraction failed, attempting 7z CLI extraction for '{sourceRelDirPath}'").ConfigureAwait(false);
                            if (await ArchiveHelper.TryExtractWithSevenZipCliAsync(archive.FullName, destPath, extracted).ConfigureAwait(false))
                            {
                                await Logger.LogAsync($"Successfully extracted via 7z CLI: '{sourceRelDirPath}'").ConfigureAwait(false);
                                return;
                            }

                            throw new InvalidOperationException($"Failed to extract '{sourceRelDirPath}': Not a valid 7z SFX or 7z CLI not available. On Linux/macOS, install 7z package (e.g., 'apt install p7zip-full' or 'brew install p7zip').");
                        }

                        // Only execute a Windows SFX when the file begins with the MZ PE header,
                        // preventing arbitrary .exe files in the mod directory from being run.
                        if (!ArchiveHelper.IsPotentialSevenZipSFX(archive.FullName))
                        {
                            throw new InvalidOperationException($"'{sourceRelDirPath}' does not appear to be a self-extracting executable (missing MZ header). Cannot extract.");
                        }

                        await Logger.LogAsync($"Managed SFX extraction failed, attempting to execute SFX for '{sourceRelDirPath}'").ConfigureAwait(false);
                        (int exitCode, string _, string _) = await PlatformAgnosticMethods.ExecuteProcessAsync(archive.FullName, $" -o\"{archive.DirectoryName}\" -y").ConfigureAwait(false);

                        if (exitCode == 0)
                        {
                            return;
                        }

                        throw new InvalidOperationException($"'{sourceRelDirPath}' is not a valid 7z self-extracting executable. Cannot extract.");
                    }

                    string extractRootDirectory = isExplicitDestination
                        ? Path.GetFullPath(destPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : Path.GetFullPath(Path.Combine(destPath, Path.GetFileNameWithoutExtension(archive.Name)))
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    using (FileStream stream = File.OpenRead(archive.FullName))
                    {
                        IArchive arch = GetArchiveByExtension(archive.Extension, stream);

                        using (arch)
                        {
                            foreach (IArchiveEntry entry in arch.Entries)
                            {
                                if (entry.IsDirectory)
                                {
                                    continue;
                                }

                                if (!PathHelper.TryGetZipSafeArchiveEntryExtractPath(
                                        extractRootDirectory,
                                        entry.Key,
                                        out string destinationItemPath,
                                        out string destinationDirectory))
                                {
                                    await Logger.LogWarningAsync($"Skipping archive entry with unsafe path: '{entry.Key}'").ConfigureAwait(false);
                                    continue;
                                }

                                if (MainConfig.CaseInsensitivePathing && !Directory.Exists(destinationDirectory))
                                {
                                    destinationDirectory = PathHelper.GetCaseSensitivePath(destinationDirectory, isFile: false).Item1;
                                }

                                string destinationRelDirPath = MainConfig.SourcePath is null ? destinationDirectory : PathHelper.GetRelativePath(MainConfig.SourcePath.FullName, destinationDirectory);

                                if (!Directory.Exists(destinationDirectory))
                                {
                                    await Logger.LogVerboseAsync($"Create directory '{destinationRelDirPath}'").ConfigureAwait(false);
                                    _ = Directory.CreateDirectory(destinationDirectory);
                                }

                                await Logger.LogVerboseAsync($"Extract '{entry.Key}' to '{destinationRelDirPath}'").ConfigureAwait(false);

                                try
                                {
                                    IArchiveEntry localEntry = entry;
                                    await Task.Run(() =>
                                    {
                                        using (Stream sourceStream = localEntry.OpenEntryStream())
                                        using (FileStream destinationStream = new FileStream(destinationItemPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                        {
                                            sourceStream.CopyTo(destinationStream);
                                        }

                                        DateTime? lastModifiedTime = localEntry.LastModifiedTime;
                                        if (lastModifiedTime.HasValue && lastModifiedTime.Value != default(DateTime))
                                        {
                                            File.SetLastWriteTime(destinationItemPath, lastModifiedTime.Value);
                                        }
                                    }, token).ConfigureAwait(false);

                                    extracted.Add(destinationItemPath);
                                }
                                catch (ObjectDisposedException ex)
                                {
                                    // A bare `return` here abandoned the whole REMAINING archive and still reported
                                    // success: the caller received a partial `extractedFiles` list with no warning and
                                    // no exception. Because the destination directory is created before the write, the
                                    // observable result was a directory tree containing zero files -- the "empty
                                    // extracted folders" in the mod library are exactly this, frozen at the first
                                    // entry. A mod that silently extracts nothing installs nothing while every step
                                    // reports OK, which is the worst possible failure mode for an installer.
                                    await Logger.LogErrorAsync(
                                        $"Extraction of '{sourcePath}' was aborted at entry '{entry.Key}': the archive stream was disposed mid-extraction. "
                                        + $"{extracted.Count} file(s) had been written; the rest of the archive was NOT extracted."
                                    ).ConfigureAwait(false);
                                    throw new IOException(
                                        $"Archive '{sourcePath}' was only partially extracted (aborted at '{entry.Key}').", ex);
                                }
                                catch (UnauthorizedAccessException ex)
                                {
                                    // Skipping a file and continuing is also a silent partial extraction: the caller
                                    // cannot distinguish "extracted everything" from "extracted everything except the
                                    // files it could not write". Fail loudly instead and let the caller decide.
                                    await Logger.LogErrorAsync(
                                        $"Extraction of '{sourcePath}' failed at entry '{entry.Key}': permission denied writing '{destinationItemPath}'."
                                    ).ConfigureAwait(false);
                                    throw new IOException(
                                        $"Archive '{sourcePath}' could not be fully extracted: permission denied for '{entry.Key}'.", ex);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _ = sem.Release();
                }
            }

            IArchive GetArchiveByExtension(string extension, Stream stream) =>
                ArchiveHelper.OpenArchiveFromStream(extension, stream);
        }

        public List<string> GetFilesInDirectory(string directoryPath, string searchPattern = "*.*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => !Directory.Exists(directoryPath)
                ? new List<string>()
                : Directory.GetFiles(directoryPath, searchPattern, searchOption).ToList();

        public List<string> GetDirectoriesInDirectory(string directoryPath) => !Directory.Exists(directoryPath) ? new List<string>() : Directory.GetDirectories(directoryPath).ToList();

        public string GetFileName(string path) => Path.GetFileName(path);

        public string GetDirectoryName(string path) => Path.GetDirectoryName(path);

        public async Task<(int exitCode, string output, string error)> ExecuteProcessAsync(string programPath, string arguments) => await PlatformAgnosticMethods.ExecuteProcessAsync(programPath, arguments).ConfigureAwait(false);

        public string GetActualPath(string path) => path;
    }
}
