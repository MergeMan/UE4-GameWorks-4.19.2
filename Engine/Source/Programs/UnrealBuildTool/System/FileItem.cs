// Copyright 1998-2014 Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;

namespace UnrealBuildTool
{
	/**
	 * Represents a file on disk that is used as an input or output of a build action.
	 * FileItems are created by calling FileItem.GetItemByPath, which creates a single FileItem for each unique file path.
	 */
	public class FileItem
	{
		/** The action that produces the file. */
		public Action ProducingAction = null;

		/** The absolute path of the file. */
		public readonly string AbsolutePath;

		/** PCH header file name as it appears in an #include statement in source code (might include partial, or no relative path.)
		    This is needed by some compilers to use PCH features. */
		public string PCHHeaderNameInCode;

		/** The pch file that this file will use */
		public string PrecompiledHeaderIncludeFilename;

		/** Description of file, used for logging. */
		public string Description;

		/** The information about the file. */
		public FileInfo Info;

		/** This is true if this item is actually a directory. Consideration for Mac application bundles. Note that Info will be null if true! */
		public bool IsDirectory;

		/** Relative cost of action associated with producing this file. */
		public long RelativeCost = 0;

		/** Used for performance debugging */
		public static long TotalFileItemCount = 0;
		public static long MissingFileItemCount = 0;

		/** True if any DLLs produced by this  */
		public bool bNeedsHotReloadNumbersDLLCleanUp = false;

		/** A case-insensitive dictionary that's used to map each unique file name to a single FileItem object. */
		static Dictionary<string, FileItem> UniqueSourceFileMap = new Dictionary<string, FileItem>();

		/** Whether or not this is a remote file, in which case we can't access it directly */
		public bool bIsRemoteFile = false;

		/** A list of remote file items that have been created but haven't needed the remote info yet, so we can gang up many into one request */
		static List<FileItem> DelayedRemoteLookupFiles = new List<FileItem>();

		/** The compile environment for this file.  This is only set on C++ source file file items, or C++-related files that have include paths.  It's part of UBT's dependency caching optimizations. */
		public CPPEnvironment CachedCPPEnvironment
		{
			get
			{
				return _CachedCPPEnvironment;
			}
			set
			{
				if( value != null && _CachedCPPEnvironment != null && _CachedCPPEnvironment != value )
				{
					// Uh oh.  We're clobbering our cached CompileEnvironment for this file with a different CompileEnvironment.  This means
					// that the same source file is being compiled into more than one module. (e.g. PCLaunch.rc)

					// @todo fastubt: The only expected offender here is PCLaunch.rc and friends, which are injected by UBT into every module when not compiling monolithic.
					// PCLaunch.rc and ModuleVersionResource.rc.inl are "safe" because they do not include any headers that would be affected by include path order.
					// ==> Ideally we would use a different "shared" CompileEnvironment for these injected .rc files, so their include paths would not change
					// ==> OR, we can make an Intermediate copy of the .rc file for each module (easier)
					if( !AbsolutePath.EndsWith( "PCLaunch.rc", StringComparison.InvariantCultureIgnoreCase ) &&
						!AbsolutePath.EndsWith( "ModuleVersionResource.rc.inl", StringComparison.InvariantCultureIgnoreCase ) )
					{ 					
						// Let's make sure the include paths are the same
						// @todo fastubt: We have not seen examples of this actually firing off, so we could probably remove the check for matching includes and simply always make this an error case
						var CachedIncludePathsToSearch = _CachedCPPEnvironment.GetIncludesPathsToSearch( this );
						var NewIncludePathsToSearch = value.GetIncludesPathsToSearch( this );

						bool bIncludesAreDifferent = false;
						if( CachedIncludePathsToSearch.Count != NewIncludePathsToSearch.Count )
						{
							bIncludesAreDifferent = true;
						}
						else
						{
							for( var IncludeIndex = 0; IncludeIndex < CachedIncludePathsToSearch.Count; ++IncludeIndex )
							{
								if( !CachedIncludePathsToSearch[ IncludeIndex ].Equals( NewIncludePathsToSearch[ IncludeIndex ], StringComparison.InvariantCultureIgnoreCase ) )
								{
									bIncludesAreDifferent = true;
									break;
								}
							}
						}

						if( bIncludesAreDifferent )
						{ 
							throw new BuildException( "File '{0}' was included by multiple modules, but with different include paths", this.Info.FullName );
						}
					}
				}
				_CachedCPPEnvironment = value;
			}
		}
		public CPPEnvironment _CachedCPPEnvironment;

		/** The last write time of the file. */
		public DateTimeOffset _LastWriteTime;
		public DateTimeOffset LastWriteTime
		{
			get
			{
				LookupOutstandingFiles();
				return _LastWriteTime;
			}
			set { _LastWriteTime = value; }
		}

		/** Whether the file exists. */
		public bool _bExists = false;
		public bool bExists
		{
			get
			{
				LookupOutstandingFiles();
				return _bExists;
			}
			set { _bExists = value; }
		}

		/** Size of the file if it exists, otherwise -1 */
		public long _Length = -1;
		public long Length
		{
			get
			{
				LookupOutstandingFiles();
				return _Length;
			}
			set { _Length = value; }
		}

		/**
		 * Resolve any outstanding remote file info lookups
		 */
		private void LookupOutstandingFiles()
		{
			// for remote files, look up any outstanding files
			if (bIsRemoteFile)
			{
				FileItem[] Files = null;
				lock (DelayedRemoteLookupFiles)
				{
					if (DelayedRemoteLookupFiles.Count > 0)
					{
						// make an array so we can clear the original array, just in case BatchFileInfo does something that uses
						// DelayedRemoteLookupFiles, so we don't deadlock
						Files = DelayedRemoteLookupFiles.ToArray();
						DelayedRemoteLookupFiles.Clear();
					}
				}
				if (Files != null)
				{
					RPCUtilHelper.BatchFileInfo(Files);
				}
			}
		}

		/** @return The FileItem that represents the given file path. */
		public static FileItem GetItemByPath(string FilePath)
		{
			var FullPath = Path.GetFullPath( FilePath );
			return GetItemByFullPath( FullPath );
		}

		/** @return The FileItem that represents the given a full file path. */
		public static FileItem GetItemByFullPath( string FullPath )
		{
			FileItem Result = null;
			if( UniqueSourceFileMap.TryGetValue( FullPath.ToLowerInvariant(), out Result ) )
			{
				return Result;
			}
			else
			{
				return new FileItem( FullPath );
			}
		}

		/** @return The remote FileItem that represents the given file path. */
		public static FileItem GetRemoteItemByPath(string AbsoluteRemotePath, UnrealTargetPlatform Platform)
		{
			if (AbsoluteRemotePath.StartsWith("."))
			{
				throw new BuildException("GetRemoteItemByPath must be passed an absolute path, not a relative path '{0}'", AbsoluteRemotePath);
			}

			string InvariantPath = AbsoluteRemotePath.ToLowerInvariant();
			FileItem Result = null;
			if (UniqueSourceFileMap.TryGetValue(InvariantPath, out Result))
			{
				return Result;
			}
			else
			{
				return new FileItem(AbsoluteRemotePath, true, Platform);
			}
		}

		/** If the given file path identifies a file that already exists, returns the FileItem that represents it. */
		public static FileItem GetExistingItemByPath(string Path)
		{
			FileItem Result = GetItemByPath(Path);
			if (Result.bExists)
			{
				return Result;
			}
			else
			{
				return null;
			}
		}

		/// <summary>
		/// Determines the appropriate encoding for a string: either ASCII or UTF-8.
		/// </summary>
		/// <param name="Str">The string to test.</param>
		/// <returns>Either System.Text.Encoding.ASCII or System.Text.Encoding.UTF8, depending on whether or not the string contains non-ASCII characters.</returns>
		private static Encoding GetEncodingForString(string Str)
		{
			// If the string length is equivalent to the encoded length, then no non-ASCII characters were present in the string.
			return (Encoding.UTF8.GetByteCount(Str) == Str.Length) ? Encoding.ASCII : Encoding.UTF8;
		}

		/**
		 * Creates a text file with the given contents.  If the contents of the text file aren't changed, it won't write the new contents to
		 * the file to avoid causing an action to be considered outdated.
		 */
		public static FileItem CreateIntermediateTextFile(string AbsolutePath, string Contents)
		{
			// Create the directory if it doesn't exist.
			Directory.CreateDirectory(Path.GetDirectoryName(AbsolutePath));

			// Only write the file if its contents have changed.
			if (!File.Exists(AbsolutePath) || Utils.ReadAllText(AbsolutePath) != Contents)
			{
				File.WriteAllText(AbsolutePath, Contents, GetEncodingForString(Contents));
			}

			return GetItemByPath(AbsolutePath);
		}

		/** Deletes the file. */
		public void Delete()
		{
			Debug.Assert(_bExists);
			Debug.Assert(!bIsRemoteFile);

			int MaxRetryCount = 3;
			int DeleteTryCount = 0;
			bool bFileDeletedSuccessfully = false;
			do
			{
				// If this isn't the first time through, sleep a little before trying again
				if( DeleteTryCount > 0 )
				{
					Thread.Sleep( 1000 );
				}
				DeleteTryCount++;
				try
				{
					// Delete the destination file if it exists
					FileInfo DeletedFileInfo = new FileInfo( AbsolutePath );
					if( DeletedFileInfo.Exists )
					{
						DeletedFileInfo.IsReadOnly = false;
						DeletedFileInfo.Delete();
					}
					// Success!
					bFileDeletedSuccessfully = true;
				}
				catch( Exception Ex )
				{
					Log.TraceInformation( "Failed to delete file '" + AbsolutePath + "'" );
					Log.TraceInformation( "    Exception: " + Ex.Message );
					if( DeleteTryCount < MaxRetryCount )
					{
						Log.TraceInformation( "Attempting to retry..." );
					}
					else
					{
						Log.TraceInformation( "ERROR: Exhausted all retries!" );
					}
				}
			}
			while( !bFileDeletedSuccessfully && ( DeleteTryCount < MaxRetryCount ) );
		}

		/** Initialization constructor. */
		protected FileItem (string FileAbsolutePath)
		{
			AbsolutePath = FileAbsolutePath;

			ResetFileInfo();

			++TotalFileItemCount;
			if (!_bExists)
			{
				++MissingFileItemCount;
				// Log.TraceInformation( "Missing: " + FileAbsolutePath );
			}

			UniqueSourceFileMap[ AbsolutePath.ToLowerInvariant() ] = this;
		}

		/// <summary>
		/// (Re-)set file information for this FileItem
		/// </summary>
		public void ResetFileInfo()
		{
			if (Directory.Exists(AbsolutePath))
			{
				// path is actually a directory (such as a Mac app bundle)
				_bExists = true;
				LastWriteTime = Directory.GetLastWriteTimeUtc(AbsolutePath);
				IsDirectory = true;
				_Length = 0;
				Info = null;
			}
			else
			{
				Info = new FileInfo(AbsolutePath);

				_bExists = Info.Exists;
				if (_bExists)
				{
					_LastWriteTime = Info.LastWriteTimeUtc;
					_Length = Info.Length;
				}
			}
		}

		/// <summary>
		/// Reset file information on all cached FileItems.
		/// </summary>
		public static void ResetInfos()
		{
			foreach (var Item in UniqueSourceFileMap)
			{
				Item.Value.ResetFileInfo();
			}
		}

		/** Initialization constructor for optionally remote files. */
		protected FileItem(string InAbsolutePath, bool InIsRemoteFile, UnrealTargetPlatform Platform)
		{
			bIsRemoteFile = InIsRemoteFile;
			AbsolutePath = InAbsolutePath;

			// @todo iosmerge: This doesn't handle remote directories (may be needed for compiling Mac from Windows)
			if (bIsRemoteFile)
			{
				if (Platform == UnrealTargetPlatform.IOS || Platform == UnrealTargetPlatform.Mac)
				{
					lock (DelayedRemoteLookupFiles)
					{
						DelayedRemoteLookupFiles.Add(this);
					}
				}
				else
				{
					throw new BuildException("Only IPhone and Mac support remote FileItems");
				}
			}
			else
			{
				FileInfo Info = new FileInfo(AbsolutePath);

				_bExists = Info.Exists;
				if (_bExists)
				{
					_LastWriteTime = Info.LastWriteTimeUtc;
					_Length = Info.Length;
				}

			    ++TotalFileItemCount;
			    if( !_bExists )
			    {
				    ++MissingFileItemCount;
				    // Log.TraceInformation( "Missing: " + FileAbsolutePath );
			    }
			}

			// @todo iosmerge: This was in UE3, why commented out now?
			//UniqueSourceFileMap[AbsolutePathUpperInvariant] = this;
		}

		public override string ToString()
		{
			return Path.GetFileName(AbsolutePath);
		}
	}

}
