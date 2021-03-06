using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Penumbra.Models;

namespace Penumbra.Mods
{
    public class ModManager : IDisposable
    {
        private readonly Plugin                         _plugin;
        public readonly  Dictionary< string, FileInfo > ResolvedFiles = new();
        public readonly  Dictionary< string, string >   SwappedFiles  = new();

        public ModCollection Mods { get; set; }

        private DirectoryInfo _basePath;

        public ModManager( Plugin plugin ) => _plugin = plugin;

        public void DiscoverMods()
        {
            if( _basePath == null )
            {
                return;
            }

            DiscoverMods( _basePath );
        }

//         private void FileSystemWatcherOnChanged( object sender, FileSystemEventArgs e )
//         {
// #if DEBUG
//             PluginLog.Verbose( "file changed: {FullPath}", e.FullPath );
// #endif
//
//             if( _plugin.ImportInProgress )
//             {
//                 return;
//             }
//
//             if( _plugin.Configuration.DisableFileSystemNotifications )
//             {
//                 return;
//             }
//
//             var file = e.FullPath;
//
//             if( !ResolvedFiles.Any( x => x.Value.FullName == file ) )
//             {
//                 return;
//             }
//
//             PluginLog.Log( "a loaded file has been modified - file: {FullPath}", file );
//             _plugin.GameUtils.ReloadPlayerResources();
//         }

        public void DiscoverMods( string basePath )
        {
            DiscoverMods( new DirectoryInfo( basePath ) );
        }

        public void DiscoverMods( DirectoryInfo basePath )
        {
            if( basePath == null )
            {
                return;
            }

            if( !basePath.Exists )
            {
                Mods = null;
                return;
            }

            _basePath = basePath;

            // haha spaghet
            // _fileSystemWatcher?.Dispose();
            // _fileSystemWatcher = new FileSystemWatcher( _basePath.FullName )
            // {
            //     NotifyFilter = NotifyFilters.LastWrite |
            //                    NotifyFilters.FileName |
            //                    NotifyFilters.DirectoryName,
            //     IncludeSubdirectories = true,
            //     EnableRaisingEvents = true
            // };
            //
            // _fileSystemWatcher.Changed += FileSystemWatcherOnChanged;
            // _fileSystemWatcher.Created += FileSystemWatcherOnChanged;
            // _fileSystemWatcher.Deleted += FileSystemWatcherOnChanged;
            // _fileSystemWatcher.Renamed += FileSystemWatcherOnChanged;

            Mods = new ModCollection( basePath );
            Mods.Load();
            Mods.Save();

            CalculateEffectiveFileList();
        }

        public void CalculateEffectiveFileList()
        {
            ResolvedFiles.Clear();
            SwappedFiles.Clear();

            var registeredFiles = new Dictionary< string, string >();

            foreach( var (mod, settings) in Mods.GetOrderedAndEnabledModListWithSettings( _plugin.Configuration.InvertModListOrder ) )
            {
                mod.FileConflicts?.Clear();
                if( settings.Conf == null )
                {
                    settings.Conf = new Dictionary< string, int >();
                    Mods.Save();
                }

                ProcessModFiles( registeredFiles, mod, settings );
                ProcessSwappedFiles( registeredFiles, mod, settings );
            }

            _plugin.GameUtils.ReloadPlayerResources();
        }

        private void ProcessSwappedFiles( Dictionary< string, string > registeredFiles, ResourceMod mod, ModInfo settings )
        {
            if( mod?.Meta?.FileSwaps == null )
            {
                return;
            }

            foreach( var swap in mod.Meta.FileSwaps )
            {
                // just assume people put not fucked paths in here lol
                if( !SwappedFiles.ContainsKey( swap.Value ) )
                {
                    SwappedFiles[ swap.Key.ToLowerInvariant() ] = swap.Value;
                    registeredFiles[ swap.Key ]                 = mod.Meta.Name;
                }
                else if( registeredFiles.TryGetValue( swap.Key, out var modName ) )
                {
                    mod.AddConflict( modName, swap.Key );
                }
            }
        }

        private void ProcessModFiles( Dictionary< string, string > registeredFiles, ResourceMod mod, ModInfo settings )
        {
            var baseDir = mod.ModBasePath.FullName;

            foreach( var file in mod.ModFiles )
            {
                var relativeFilePath = file.FullName.Substring( baseDir.Length ).TrimStart( '\\' );

                var doNotAdd = false;

                HashSet< string > paths;
                foreach( var group in mod.Meta.Groups.Select( G => G.Value ) )
                {
                    if( !settings.Conf.TryGetValue( group.GroupName, out var setting )
                        || group.SelectionType == SelectType.Single
                        && settings.Conf[ group.GroupName ] >= group.Options.Count )
                    {
                        settings.Conf[ group.GroupName ] = 0;
                        Mods.Save();
                        setting = 0;
                    }

                    if( group.Options.Count == 0 )
                    {
                        continue;
                    }

                    if( group.SelectionType == SelectType.Multi )
                    {
                        settings.Conf[ group.GroupName ] &= ( 1 << group.Options.Count ) - 1;
                    }

                    switch( group.SelectionType )
                    {
                        case SelectType.Single:
                            if( group.Options[ setting ].OptionFiles.TryGetValue( relativeFilePath, out paths ) )
                            {
                                AddFiles( paths, out doNotAdd, file, registeredFiles, mod );
                            }
                            else
                            {
                                if( group.Options.Where( ( o, i ) => i != setting )
                                         .Any( option => option.OptionFiles.ContainsKey( relativeFilePath ) ) )
                                {
                                    doNotAdd = true;
                                }
                            }

                            break;
                        case SelectType.Multi:
                            for( var i = 0; i < group.Options.Count; ++i )
                            {
                                if( ( setting & ( 1 << i ) ) != 0 )
                                {
                                    if( group.Options[ i ].OptionFiles.TryGetValue( relativeFilePath, out paths ) )
                                    {
                                        AddFiles( paths, out doNotAdd, file, registeredFiles, mod );
                                    }
                                }
                                else if( group.Options[ i ].OptionFiles.ContainsKey( relativeFilePath ) )
                                {
                                    doNotAdd = true;
                                }
                            }

                            break;
                    }
                }

                if( !doNotAdd )
                {
                    AddFiles( new HashSet< string > { relativeFilePath.Replace( '\\', '/' ) }, out doNotAdd, file, registeredFiles, mod );
                }
            }
        }

        private void AddFiles( HashSet< string > gamePaths, out bool doNotAdd, FileInfo file, Dictionary< string, string > registeredFiles,
            ResourceMod mod )
        {
            doNotAdd = true;
            foreach( var gamePath in gamePaths )
            {
                if( !ResolvedFiles.ContainsKey( gamePath ) )
                {
                    ResolvedFiles[ gamePath.ToLowerInvariant() ] = file;
                    registeredFiles[ gamePath ]                  = mod.Meta.Name;
                }
                else if( registeredFiles.TryGetValue( gamePath, out var modName ) )
                {
                    mod.AddConflict( modName, gamePath );
                }
            }
        }

        public void ChangeModPriority( ModInfo info, bool up = false )
        {
            Mods.ReorderMod( info, up );
            CalculateEffectiveFileList();
        }

        public void DeleteMod( ResourceMod mod )
        {
            if( mod?.ModBasePath?.Exists ?? false )
            {
                try
                {
                    Directory.Delete( mod.ModBasePath.FullName, true );
                }
                catch( Exception e )
                {
                    PluginLog.Error( $"Could not delete the mod {mod.ModBasePath.Name}:\n{e}" );
                }
            }

            DiscoverMods();
        }

        public FileInfo GetCandidateForGameFile( string gameResourcePath )
        {
            var val = ResolvedFiles.TryGetValue( gameResourcePath, out var candidate );
            if( !val )
            {
                return null;
            }

            if( candidate.FullName.Length >= 260 || !candidate.Exists )
            {
                return null;
            }

            return candidate;
        }

        public string GetSwappedFilePath( string gameResourcePath )
            => SwappedFiles.TryGetValue( gameResourcePath, out var swappedPath ) ? swappedPath : null;

        public string ResolveSwappedOrReplacementFilePath( string gameResourcePath )
        {
            gameResourcePath = gameResourcePath.ToLowerInvariant();

            return GetCandidateForGameFile( gameResourcePath )?.FullName ?? GetSwappedFilePath( gameResourcePath );
        }


        public void Dispose()
        {
            // _fileSystemWatcher?.Dispose();
        }
    }
}