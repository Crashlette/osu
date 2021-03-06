﻿// Copyright (c) 2007-2017 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Ionic.Zip;
using Microsoft.EntityFrameworkCore;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Beatmaps.Formats;
using osu.Game.Beatmaps.IO;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.IO;
using osu.Game.IPC;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// Handles the storage and retrieval of Beatmaps/WorkingBeatmaps.
    /// </summary>
    public class BeatmapManager
    {
        /// <summary>
        /// Fired when a new <see cref="BeatmapSetInfo"/> becomes available in the database.
        /// </summary>
        public event Action<BeatmapSetInfo> BeatmapSetAdded;

        /// <summary>
        /// Fired when a single difficulty has been hidden.
        /// </summary>
        public event Action<BeatmapInfo> BeatmapHidden;

        /// <summary>
        /// Fired when a <see cref="BeatmapSetInfo"/> is removed from the database.
        /// </summary>
        public event Action<BeatmapSetInfo> BeatmapSetRemoved;

        /// <summary>
        /// Fired when a single difficulty has been restored.
        /// </summary>
        public event Action<BeatmapInfo> BeatmapRestored;

        /// <summary>
        /// Fired when a beatmap download begins.
        /// </summary>
        public event Action<DownloadBeatmapSetRequest> BeatmapDownloadBegan;

        /// <summary>
        /// A default representation of a WorkingBeatmap to use when no beatmap is available.
        /// </summary>
        public WorkingBeatmap DefaultBeatmap { private get; set; }

        private readonly Storage storage;

        private BeatmapStore createBeatmapStore(Func<OsuDbContext> context)
        {
            var store = new BeatmapStore(context);
            store.BeatmapSetAdded += s => BeatmapSetAdded?.Invoke(s);
            store.BeatmapSetRemoved += s => BeatmapSetRemoved?.Invoke(s);
            store.BeatmapHidden += b => BeatmapHidden?.Invoke(b);
            store.BeatmapRestored += b => BeatmapRestored?.Invoke(b);
            return store;
        }

        private readonly Func<OsuDbContext> createContext;

        private readonly FileStore files;

        private readonly RulesetStore rulesets;

        private readonly BeatmapStore beatmaps;

        private readonly APIAccess api;

        private readonly List<DownloadBeatmapSetRequest> currentDownloads = new List<DownloadBeatmapSetRequest>();

        // ReSharper disable once NotAccessedField.Local (we should keep a reference to this so it is not finalised)
        private BeatmapIPCChannel ipc;

        /// <summary>
        /// Set an endpoint for notifications to be posted to.
        /// </summary>
        public Action<Notification> PostNotification { private get; set; }

        /// <summary>
        /// Set a storage with access to an osu-stable install for import purposes.
        /// </summary>
        public Func<Storage> GetStableStorage { private get; set; }

        public BeatmapManager(Storage storage, Func<OsuDbContext> context, RulesetStore rulesets, APIAccess api, IIpcHost importHost = null)
        {
            createContext = context;
            importContext = new Lazy<OsuDbContext>(() =>
            {
                var c = createContext();
                c.Database.AutoTransactionsEnabled = false;
                return c;
            });

            beatmaps = createBeatmapStore(context);
            files = new FileStore(context, storage);

            this.storage = files.Storage;
            this.rulesets = rulesets;
            this.api = api;

            if (importHost != null)
                ipc = new BeatmapIPCChannel(importHost, this);

            beatmaps.Cleanup();
        }

        /// <summary>
        /// Import one or more <see cref="BeatmapSetInfo"/> from filesystem <paramref name="paths"/>.
        /// This will post a notification tracking import progress.
        /// </summary>
        /// <param name="paths">One or more beatmap locations on disk.</param>
        public void Import(params string[] paths)
        {
            var notification = new ProgressNotification
            {
                Text = "Beatmap import is initialising...",
                Progress = 0,
                State = ProgressNotificationState.Active,
            };

            PostNotification?.Invoke(notification);

            int i = 0;
            foreach (string path in paths)
            {
                if (notification.State == ProgressNotificationState.Cancelled)
                    // user requested abort
                    return;

                try
                {
                    notification.Text = $"Importing ({i} of {paths.Length})\n{Path.GetFileName(path)}";
                    using (ArchiveReader reader = getReaderFrom(path))
                        Import(reader);

                    notification.Progress = (float)++i / paths.Length;

                    // We may or may not want to delete the file depending on where it is stored.
                    //  e.g. reconstructing/repairing database with beatmaps from default storage.
                    // Also, not always a single file, i.e. for LegacyFilesystemReader
                    // TODO: Add a check to prevent files from storage to be deleted.
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, $@"Could not delete original file after import ({Path.GetFileName(path)})");
                    }
                }
                catch (Exception e)
                {
                    e = e.InnerException ?? e;
                    Logger.Error(e, $@"Could not import beatmap set ({Path.GetFileName(path)})");
                }
            }

            notification.State = ProgressNotificationState.Completed;
        }

        private readonly Lazy<OsuDbContext> importContext;

        /// <summary>
        /// Import a beatmap from an <see cref="ArchiveReader"/>.
        /// </summary>
        /// <param name="archiveReader">The beatmap to be imported.</param>
        public BeatmapSetInfo Import(ArchiveReader archiveReader)
        {
            // let's only allow one concurrent import at a time for now.
            lock (importContext)
            {
                var context = importContext.Value;

                using (var transaction = context.BeginTransaction())
                {
                    // create local stores so we can isolate and thread safely, and share a context/transaction.
                    var iFiles = new FileStore(() => context, storage);
                    var iBeatmaps = createBeatmapStore(() => context);

                    BeatmapSetInfo set = importToStorage(iFiles, iBeatmaps, archiveReader);

                    if (set.ID == 0)
                    {
                        iBeatmaps.Add(set);
                        context.SaveChanges();
                    }

                    context.SaveChanges(transaction);
                    return set;
                }
            }
        }

        /// <summary>
        /// Import a beatmap from a <see cref="BeatmapSetInfo"/>.
        /// </summary>
        /// <param name="beatmapSetInfo">The beatmap to be imported.</param>
        public void Import(BeatmapSetInfo beatmapSetInfo)
        {
            // If we have an ID then we already exist in the database.
            if (beatmapSetInfo.ID != 0) return;

            createBeatmapStore(createContext).Add(beatmapSetInfo);
        }

        /// <summary>
        /// Downloads a beatmap.
        /// </summary>
        /// <param name="beatmapSetInfo">The <see cref="BeatmapSetInfo"/> to be downloaded.</param>
        /// <param name="noVideo">Whether the beatmap should be downloaded without video. Defaults to false.</param>
        public void Download(BeatmapSetInfo beatmapSetInfo, bool noVideo = false)
        {
            var existing = GetExistingDownload(beatmapSetInfo);

            if (existing != null || api == null) return;

            if (!api.LocalUser.Value.IsSupporter)
            {
                PostNotification?.Invoke(new SimpleNotification
                {
                    Icon = FontAwesome.fa_superpowers,
                    Text = "You gotta be a supporter to download for now 'yo"
                });
                return;
            }

            ProgressNotification downloadNotification = new ProgressNotification
            {
                Text = $"Downloading {beatmapSetInfo.Metadata.Artist} - {beatmapSetInfo.Metadata.Title}",
            };

            var request = new DownloadBeatmapSetRequest(beatmapSetInfo, noVideo);

            request.DownloadProgressed += progress =>
            {
                downloadNotification.State = ProgressNotificationState.Active;
                downloadNotification.Progress = progress;
            };

            request.Success += data =>
            {
                downloadNotification.Text = $"Importing {beatmapSetInfo.Metadata.Artist} - {beatmapSetInfo.Metadata.Title}";

                Task.Factory.StartNew(() =>
                {
                    // This gets scheduled back to the update thread, but we want the import to run in the background.
                    using (var stream = new MemoryStream(data))
                    using (var archive = new OszArchiveReader(stream))
                        Import(archive);

                    downloadNotification.State = ProgressNotificationState.Completed;
                }, TaskCreationOptions.LongRunning);

                currentDownloads.Remove(request);
            };

            request.Failure += data =>
            {
                downloadNotification.State = ProgressNotificationState.Completed;
                Logger.Error(data, "Failed to get beatmap download information");
                currentDownloads.Remove(request);
            };

            downloadNotification.CancelRequested += () =>
            {
                request.Cancel();
                currentDownloads.Remove(request);
                downloadNotification.State = ProgressNotificationState.Cancelled;
                return true;
            };

            currentDownloads.Add(request);
            PostNotification?.Invoke(downloadNotification);

            // don't run in the main api queue as this is a long-running task.
            Task.Factory.StartNew(() => request.Perform(api), TaskCreationOptions.LongRunning);
            BeatmapDownloadBegan?.Invoke(request);
        }

        /// <summary>
        /// Get an existing download request if it exists.
        /// </summary>
        /// <param name="beatmap">The <see cref="BeatmapSetInfo"/> whose download request is wanted.</param>
        /// <returns>The <see cref="DownloadBeatmapSetRequest"/> object if it exists, or null.</returns>
        public DownloadBeatmapSetRequest GetExistingDownload(BeatmapSetInfo beatmap) => currentDownloads.Find(d => d.BeatmapSet.OnlineBeatmapSetID == beatmap.OnlineBeatmapSetID);

        /// <summary>
        /// Delete a beatmap from the manager.
        /// Is a no-op for already deleted beatmaps.
        /// </summary>
        /// <param name="beatmapSet">The beatmap set to delete.</param>
        public void Delete(BeatmapSetInfo beatmapSet)
        {
            lock (importContext)
            {
                var context = importContext.Value;

                using (var transaction = context.BeginTransaction())
                {
                    context.ChangeTracker.AutoDetectChangesEnabled = false;

                    // re-fetch the beatmap set on the import context.
                    beatmapSet = context.BeatmapSetInfo.Include(s => s.Files).ThenInclude(f => f.FileInfo).First(s => s.ID == beatmapSet.ID);

                    // create local stores so we can isolate and thread safely, and share a context/transaction.
                    var iFiles = new FileStore(() => context, storage);
                    var iBeatmaps = createBeatmapStore(() => context);

                    if (iBeatmaps.Delete(beatmapSet))
                    {
                        if (!beatmapSet.Protected)
                            iFiles.Dereference(beatmapSet.Files.Select(f => f.FileInfo).ToArray());
                    }

                    context.ChangeTracker.AutoDetectChangesEnabled = true;
                    context.SaveChanges(transaction);
                }
            }
        }

        /// <summary>
        /// Delete a beatmap difficulty.
        /// </summary>
        /// <param name="beatmap">The beatmap difficulty to hide.</param>
        public void Hide(BeatmapInfo beatmap) => beatmaps.Hide(beatmap);

        /// <summary>
        /// Restore a beatmap difficulty.
        /// </summary>
        /// <param name="beatmap">The beatmap difficulty to restore.</param>
        public void Restore(BeatmapInfo beatmap) => beatmaps.Restore(beatmap);

        /// <summary>
        /// Returns a <see cref="BeatmapSetInfo"/> to a usable state if it has previously been deleted but not yet purged.
        /// Is a no-op for already usable beatmaps.
        /// </summary>
        /// <param name="beatmaps">The store to restore beatmaps from.</param>
        /// <param name="files">The store to restore beatmap files from.</param>
        /// <param name="beatmapSet">The beatmap to restore.</param>
        private void undelete(BeatmapStore beatmaps, FileStore files, BeatmapSetInfo beatmapSet)
        {
            if (!beatmaps.Undelete(beatmapSet)) return;

            if (!beatmapSet.Protected)
                files.Reference(beatmapSet.Files.Select(f => f.FileInfo).ToArray());
        }

        /// <summary>
        /// Retrieve a <see cref="WorkingBeatmap"/> instance for the provided <see cref="BeatmapInfo"/>
        /// </summary>
        /// <param name="beatmapInfo">The beatmap to lookup.</param>
        /// <param name="previous">The currently loaded <see cref="WorkingBeatmap"/>. Allows for optimisation where elements are shared with the new beatmap.</param>
        /// <returns>A <see cref="WorkingBeatmap"/> instance correlating to the provided <see cref="BeatmapInfo"/>.</returns>
        public WorkingBeatmap GetWorkingBeatmap(BeatmapInfo beatmapInfo, WorkingBeatmap previous = null)
        {
            if (beatmapInfo == null || beatmapInfo == DefaultBeatmap?.BeatmapInfo)
                return DefaultBeatmap;

            if (beatmapInfo.BeatmapSet == null)
                throw new InvalidOperationException($@"Beatmap set {beatmapInfo.BeatmapSetInfoID} is not in the local database.");

            if (beatmapInfo.Metadata == null)
                beatmapInfo.Metadata = beatmapInfo.BeatmapSet.Metadata;

            WorkingBeatmap working = new BeatmapManagerWorkingBeatmap(files.Store, beatmapInfo);

            previous?.TransferTo(working);

            return working;
        }

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapSetInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The first result for the provided query, or null if no results were found.</returns>
        public BeatmapSetInfo QueryBeatmapSet(Expression<Func<BeatmapSetInfo, bool>> query) => beatmaps.BeatmapSets.AsNoTracking().FirstOrDefault(query);

        /// <summary>
        /// Refresh an existing instance of a <see cref="BeatmapSetInfo"/> from the store.
        /// </summary>
        /// <param name="beatmapSet">A stale instance.</param>
        /// <returns>A fresh instance.</returns>
        public BeatmapSetInfo Refresh(BeatmapSetInfo beatmapSet) => QueryBeatmapSet(s => s.ID == beatmapSet.ID);

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapSetInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>Results from the provided query.</returns>
        public IEnumerable<BeatmapSetInfo> QueryBeatmapSets(Expression<Func<BeatmapSetInfo, bool>> query) => beatmaps.BeatmapSets.AsNoTracking().Where(query);

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The first result for the provided query, or null if no results were found.</returns>
        public BeatmapInfo QueryBeatmap(Expression<Func<BeatmapInfo, bool>> query) => beatmaps.Beatmaps.AsNoTracking().FirstOrDefault(query);

        /// <summary>
        /// Perform a lookup query on available <see cref="BeatmapInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>Results from the provided query.</returns>
        public IEnumerable<BeatmapInfo> QueryBeatmaps(Expression<Func<BeatmapInfo, bool>> query) => beatmaps.Beatmaps.AsNoTracking().Where(query);

        /// <summary>
        /// Creates an <see cref="ArchiveReader"/> from a valid storage path.
        /// </summary>
        /// <param name="path">A file or folder path resolving the beatmap content.</param>
        /// <returns>A reader giving access to the beatmap's content.</returns>
        private ArchiveReader getReaderFrom(string path)
        {
            if (ZipFile.IsZipFile(path))
                // ReSharper disable once InconsistentlySynchronizedField
                return new OszArchiveReader(storage.GetStream(path));
            return new LegacyFilesystemReader(path);
        }

        /// <summary>
        /// Import a beamap into our local <see cref="FileStore"/> storage.
        /// If the beatmap is already imported, the existing instance will be returned.
        /// </summary>
        /// <param name="files">The store to import beatmap files to.</param>
        /// <param name="beatmaps">The store to import beatmaps to.</param>
        /// <param name="reader">The beatmap archive to be read.</param>
        /// <returns>The imported beatmap, or an existing instance if it is already present.</returns>
        private BeatmapSetInfo importToStorage(FileStore files, BeatmapStore beatmaps, ArchiveReader reader)
        {
            // let's make sure there are actually .osu files to import.
            string mapName = reader.Filenames.FirstOrDefault(f => f.EndsWith(".osu"));
            if (string.IsNullOrEmpty(mapName))
                throw new InvalidOperationException("No beatmap files found in the map folder.");

            // for now, concatenate all .osu files in the set to create a unique hash.
            MemoryStream hashable = new MemoryStream();
            foreach (string file in reader.Filenames.Where(f => f.EndsWith(".osu")))
                using (Stream s = reader.GetStream(file))
                    s.CopyTo(hashable);

            var hash = hashable.ComputeSHA2Hash();

            // check if this beatmap has already been imported and exit early if so.
            var beatmapSet = beatmaps.BeatmapSets.FirstOrDefault(b => b.Hash == hash);

            if (beatmapSet != null)
            {
                undelete(beatmaps, files, beatmapSet);

                // ensure all files are present and accessible
                foreach (var f in beatmapSet.Files)
                {
                    if (!storage.Exists(f.FileInfo.StoragePath))
                        using (Stream s = reader.GetStream(f.Filename))
                            files.Add(s, false);
                }

                // todo: delete any files which shouldn't exist any more.

                return beatmapSet;
            }

            List<BeatmapSetFileInfo> fileInfos = new List<BeatmapSetFileInfo>();

            // import files to manager
            foreach (string file in reader.Filenames)
                using (Stream s = reader.GetStream(file))
                    fileInfos.Add(new BeatmapSetFileInfo
                    {
                        Filename = file,
                        FileInfo = files.Add(s)
                    });

            BeatmapMetadata metadata;

            using (var stream = new StreamReader(reader.GetStream(mapName)))
                metadata = BeatmapDecoder.GetDecoder(stream).Decode(stream).Metadata;

            // check if a set already exists with the same online id.
            beatmapSet = beatmaps.BeatmapSets.FirstOrDefault(b => b.OnlineBeatmapSetID == metadata.OnlineBeatmapSetID) ?? new BeatmapSetInfo
            {
                OnlineBeatmapSetID = metadata.OnlineBeatmapSetID,
                Beatmaps = new List<BeatmapInfo>(),
                Hash = hash,
                Files = fileInfos,
                Metadata = metadata
            };

            var mapNames = reader.Filenames.Where(f => f.EndsWith(".osu"));

            foreach (var name in mapNames)
            {
                using (var raw = reader.GetStream(name))
                using (var ms = new MemoryStream()) //we need a memory stream so we can seek and shit
                using (var sr = new StreamReader(ms))
                {
                    raw.CopyTo(ms);
                    ms.Position = 0;

                    var decoder = BeatmapDecoder.GetDecoder(sr);
                    Beatmap beatmap = decoder.Decode(sr);

                    beatmap.BeatmapInfo.Path = name;
                    beatmap.BeatmapInfo.Hash = ms.ComputeSHA2Hash();
                    beatmap.BeatmapInfo.MD5Hash = ms.ComputeMD5Hash();

                    var existing = beatmaps.Beatmaps.FirstOrDefault(b => b.Hash == beatmap.BeatmapInfo.Hash || b.OnlineBeatmapID == beatmap.BeatmapInfo.OnlineBeatmapID);

                    if (existing == null)
                    {
                        // TODO: Diff beatmap metadata with set metadata and leave it here if necessary
                        beatmap.BeatmapInfo.Metadata = null;

                        RulesetInfo ruleset = rulesets.GetRuleset(beatmap.BeatmapInfo.RulesetID);

                        // TODO: this should be done in a better place once we actually need to dynamically update it.
                        beatmap.BeatmapInfo.Ruleset = ruleset;
                        beatmap.BeatmapInfo.StarDifficulty = ruleset?.CreateInstance()?.CreateDifficultyCalculator(beatmap).Calculate() ?? 0;

                        beatmapSet.Beatmaps.Add(beatmap.BeatmapInfo);
                    }
                }
            }

            return beatmapSet;
        }

        /// <summary>
        /// Returns a list of all usable <see cref="BeatmapSetInfo"/>s.
        /// </summary>
        /// <returns>A list of available <see cref="BeatmapSetInfo"/>.</returns>
        public List<BeatmapSetInfo> GetAllUsableBeatmapSets()
        {
            return beatmaps.BeatmapSets.Where(s => !s.DeletePending).ToList();
        }

        protected class BeatmapManagerWorkingBeatmap : WorkingBeatmap
        {
            private readonly IResourceStore<byte[]> store;

            public BeatmapManagerWorkingBeatmap(IResourceStore<byte[]> store, BeatmapInfo beatmapInfo)
                : base(beatmapInfo)
            {
                this.store = store;
            }

            protected override Beatmap GetBeatmap()
            {
                try
                {
                    Beatmap beatmap;

                    BeatmapDecoder decoder;
                    using (var stream = new StreamReader(store.GetStream(getPathForFile(BeatmapInfo.Path))))
                    {
                        decoder = BeatmapDecoder.GetDecoder(stream);
                        beatmap = decoder.Decode(stream);
                    }

                    if (beatmap == null || BeatmapSetInfo.StoryboardFile == null)
                        return beatmap;

                    using (var stream = new StreamReader(store.GetStream(getPathForFile(BeatmapSetInfo.StoryboardFile))))
                        decoder.Decode(stream, beatmap);


                    return beatmap;
                }
                catch
                {
                    return null;
                }
            }

            private string getPathForFile(string filename) => BeatmapSetInfo.Files.First(f => string.Equals(f.Filename, filename, StringComparison.InvariantCultureIgnoreCase)).FileInfo.StoragePath;

            protected override Texture GetBackground()
            {
                if (Metadata?.BackgroundFile == null)
                    return null;

                try
                {
                    return new TextureStore(new RawTextureLoaderStore(store), false).Get(getPathForFile(Metadata.BackgroundFile));
                }
                catch
                {
                    return null;
                }
            }

            protected override Track GetTrack()
            {
                try
                {
                    var trackData = store.GetStream(getPathForFile(Metadata.AudioFile));
                    return trackData == null ? null : new TrackBass(trackData);
                }
                catch
                {
                    return new TrackVirtual();
                }
            }

            protected override Waveform GetWaveform() => new Waveform(store.GetStream(getPathForFile(Metadata.AudioFile)));
        }

        /// <summary>
        /// This is a temporary method and will likely be replaced by a full-fledged (and more correctly placed) migration process in the future.
        /// </summary>
        public void ImportFromStable()
        {
            var stable = GetStableStorage?.Invoke();

            if (stable == null)
            {
                Logger.Log("No osu!stable installation available!", LoggingTarget.Information, LogLevel.Error);
                return;
            }

            Import(stable.GetDirectories("Songs"));
        }

        public void DeleteAll()
        {
            var maps = GetAllUsableBeatmapSets();

            if (maps.Count == 0) return;

            var notification = new ProgressNotification
            {
                Progress = 0,
                State = ProgressNotificationState.Active,
            };

            PostNotification?.Invoke(notification);

            int i = 0;

            foreach (var b in maps)
            {
                if (notification.State == ProgressNotificationState.Cancelled)
                    // user requested abort
                    return;

                notification.Text = $"Deleting ({i} of {maps.Count})";
                notification.Progress = (float)++i / maps.Count;
                Delete(b);
            }

            notification.State = ProgressNotificationState.Completed;
        }
    }
}
