// Copyright 1998-2015 Epic Games, Inc. All Rights Reserved.
	
#include "LaunchPrivatePCH.h"
#include "ExceptionHandling.h"
#include "MallocCrash.h"

#if UE_BUILD_DEBUG
#include <crtdbg.h>
#endif

DEFINE_LOG_CATEGORY_STATIC(LogLaunchWindows, Log, All);

extern int32 GuardedMain( const TCHAR* CmdLine, HINSTANCE hInInstance, HINSTANCE hPrevInstance, int32 nCmdShow );
extern void LaunchStaticShutdownAfterError();

// http://developer.download.nvidia.com/devzone/devcenter/gamegraphics/files/OptimusRenderingPolicies.pdf
// The following line is to favor the high performance NVIDIA GPU if there are multiple GPUs
// Has to be .exe module to be correctly detected.
extern "C" { _declspec(dllexport) uint32 NvOptimusEnablement = 0x00000001; }

/**
 * Maintain a named mutex to detect whether we are the first instance of this game
 */
HANDLE GNamedMutex = NULL;

/** Whether we should pause before exiting. used by UCC */
bool		GShouldPauseBeforeExit;

void ReleaseNamedMutex( void )
{
	if( GNamedMutex )
	{
		ReleaseMutex( GNamedMutex );
		GNamedMutex = NULL;
	}
}

bool MakeNamedMutex( const TCHAR* CmdLine )
{
	bool bIsFirstInstance = false;

	TCHAR MutexName[MAX_SPRINTF] = TEXT( "" );

	FCString::Strcpy( MutexName, MAX_SPRINTF, TEXT( "UnrealEngine4" ) );

	GNamedMutex = CreateMutex( NULL, true, MutexName );

	if( GNamedMutex	&& GetLastError() != ERROR_ALREADY_EXISTS && !FParse::Param( CmdLine, TEXT( "NEVERFIRST" ) ) )
	{
		// We're the first instance!
		bIsFirstInstance = true;
	}
	else
	{
		// Still need to release it in this case, because it gave us a valid copy
		ReleaseNamedMutex();
		// There is already another instance of the game running.
		bIsFirstInstance = false;
	}

	return( bIsFirstInstance );
}

/**
 * Handler for CRT parameter validation. Triggers error
 *
 * @param Expression - the expression that failed crt validation
 * @param Function - function which failed crt validation
 * @param File - file where failure occured
 * @param Line - line number of failure
 * @param Reserved - not used
 */
void InvalidParameterHandler(const TCHAR* Expression,
							 const TCHAR* Function, 
							 const TCHAR* File, 
							 uint32 Line, 
							 uintptr_t Reserved)
{
	UE_LOG(LogLaunchWindows, Fatal,TEXT("SECURE CRT: Invalid parameter detected.\nExpression: %s Function: %s. File: %s Line: %d\n"), 
		Expression ? Expression : TEXT("Unknown"), 
		Function ? Function : TEXT("Unknown"), 
		File ? File : TEXT("Unknown"), 
		Line );
}

/**
 * Setup the common debug settings 
 */
void SetupWindowsEnvironment( void )
{
	// all crt validation should trigger the callback
	_set_invalid_parameter_handler(InvalidParameterHandler);

#if UE_BUILD_DEBUG
	// Disable the message box for assertions and just write to debugout instead
	_CrtSetReportMode( _CRT_ASSERT, _CRTDBG_MODE_DEBUG );
	// don't fill buffers with 0xfd as we make assumptions for FNames st we only use a fraction of the entire buffer
	_CrtSetDebugFillThreshold( 0 );
#endif
}

/**
 * The inner exception handler catches crashes/asserts in native C++ code and is the only way to get the correct callstack
 * when running a 64-bit executable. However, XAudio2 doesn't always like this and it may result in no sound.
 */
#if _WIN64
	bool GEnableInnerException = true;
#else
	bool GEnableInnerException = false;
#endif

/**
 * The inner exception handler catches crashes/asserts in native C++ code and is the only way to get the correct callstack
 * when running a 64-bit executable. However, XAudio2 doesn't like this and it may result in no sound.
 */
LAUNCH_API int32 GuardedMainWrapper( const TCHAR* CmdLine, HINSTANCE hInInstance, HINSTANCE hPrevInstance, int32 nCmdShow )
{
	int32 ErrorLevel = 0;
	if ( GEnableInnerException )
	{
#if !PLATFORM_SEH_EXCEPTIONS_DISABLED
	 	__try
#endif
		{
			// Run the guarded code.
			ErrorLevel = GuardedMain( CmdLine, hInInstance, hPrevInstance, nCmdShow );
		}
#if !PLATFORM_SEH_EXCEPTIONS_DISABLED
		__except( ReportCrash( GetExceptionInformation() ), EXCEPTION_CONTINUE_SEARCH )
		{
		}
#endif
	}
	else
	{
		// Run the guarded code.
		ErrorLevel = GuardedMain( CmdLine, hInInstance, hPrevInstance, nCmdShow );
	}
	return ErrorLevel;
}

static void MergeDefaultArgumentsIntoCommandLine(FString& CommandLine, FString DefaultArguments)
{
	// Normalize the arguments by removing any newlines and unnecessary spaces
	DefaultArguments.ReplaceInline(TEXT("\r"), TEXT(" "));
	DefaultArguments.ReplaceInline(TEXT("\n"), TEXT(" "));
	DefaultArguments = DefaultArguments.Trim();

	// We need to make sure that the project is always the first argument, otherwise it's not parsed correctly in LaunchEngineLoop (which also
	// interferes with passing a map name on the command-line). It's possible that UE4CommandLine.txt contains the game name (for packaged games) 
	// or that the command line contains the game name (for the editor), so we have to be careful in which order they're stitched together.
	if(DefaultArguments.Len() > 0)
	{
		// Measure the length of the application name in the existing command line, respecting quoted substrings.
		int32 AppNameLength = 0;
		for(bool bInQuotes = false; AppNameLength < CommandLine.Len(); AppNameLength++)
		{
			if(CommandLine[AppNameLength] == '\"')
			{
				bInQuotes ^= true;
			}
			else if(!bInQuotes && FChar::IsWhitespace(CommandLine[AppNameLength]))
			{
				break;
			}
		}

		// Measure the length of the project name in the default command line.
		int32 ProjectNameLength = 0;
		const TCHAR* RemainingArguments = *DefaultArguments;
		FString FirstArgument;
		if(FParse::Token(RemainingArguments, FirstArgument, false) && !FirstArgument.StartsWith("-"))
		{
			ProjectNameLength = RemainingArguments - *DefaultArguments;
		}

		// Build a new command line consisting of the application name, project name, command line arguments, and default arguments.
		FString NewCommandLine;
		NewCommandLine.Append(*CommandLine, AppNameLength);
		NewCommandLine.AppendChar(TEXT(' '));
		NewCommandLine.Append(*DefaultArguments, ProjectNameLength);
		NewCommandLine.Append(*CommandLine + AppNameLength);
		NewCommandLine.Append(*DefaultArguments + ProjectNameLength);
		CommandLine = NewCommandLine;
	}
}
	
static const TCHAR* GetCompleteCommandLine()
{
	static FString CommandLine;
	if(CommandLine.Len() == 0)
	{
		// Default to the native command line
		CommandLine = ::GetCommandLine();

		// Get the path to the arguments file
		FString ArgsFileName = FPaths::RootDir() / TEXT("UE4CommandLine.txt");

		// Try to read the default arguments
		HANDLE FileHandle = CreateFile(*ArgsFileName, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
		if(FileHandle == INVALID_HANDLE_VALUE)
		{
			if(GetLastError() != ERROR_FILE_NOT_FOUND)
			{
				FPlatformMisc::LowLevelOutputDebugString(*FString::Printf(TEXT("WARNING: Failed to open UE4CommandLine.txt - %08X\n"), GetLastError()));
			}
		}
		else
		{
			::DWORD FileSize = ::GetFileSize(FileHandle, NULL);
			if(FileSize == (::DWORD)0xffffffff)
			{
				FPlatformMisc::LowLevelOutputDebugString(*FString::Printf(TEXT("WARNING: Failed to get file size for UE4CommandLine.txt (%08x)\n"), GetLastError()));
			}
			else
			{
				TArray<BYTE> Buffer;
				Buffer.AddZeroed(FileSize + 1);

				::DWORD FileSizeRead = 0;
				if (!ReadFile(FileHandle, Buffer.GetData(), FileSize, &FileSizeRead, NULL) || FileSizeRead < FileSize)
				{
					FPlatformMisc::LowLevelOutputDebugString(*FString::Printf(TEXT("WARNING: Failed to read UE4CommandLine.txt file (%08x)\n"), GetLastError()));
				}
				else
				{
					MergeDefaultArgumentsIntoCommandLine(CommandLine, FString(UTF8_TO_TCHAR((const ANSICHAR*)Buffer.GetData())));
				}
			}
			CloseHandle(FileHandle);
		}
	}
	return *CommandLine;
}

int32 WINAPI WinMain( HINSTANCE hInInstance, HINSTANCE hPrevInstance, char*, int32 nCmdShow )
{
	// Setup common Windows settings
	SetupWindowsEnvironment();

	int32 ErrorLevel			= 0;
	hInstance				= hInInstance;
	const TCHAR* CmdLine = GetCompleteCommandLine();

#if !(UE_BUILD_SHIPPING && WITH_EDITOR)
	// Named mutex we use to figure out whether we are the first instance of the game running. This is needed to e.g.
	// make sure there is no contention when trying to save the shader cache.
	GIsFirstInstance = MakeNamedMutex( CmdLine );

	if ( FParse::Param( CmdLine,TEXT("crashreports") ) )
	{
		GAlwaysReportCrash = true;
	}
#endif

	// Using the -noinnerexception parameter will disable the exception handler within native C++, which is call from managed code,
	// which is called from this function.
	// The default case is to have three wrapped exception handlers 
	// Native: WinMain() -> Native: GuardedMainWrapper().
	// The inner exception handler in GuardedMainWrapper() catches crashes/asserts in native C++ code and is the only way to get the
	// correct callstack when running a 64-bit executable. However, XAudio2 sometimes (?) don't like this and it may result in no sound.
#if _WIN64
	if ( FParse::Param(CmdLine,TEXT("noinnerexception")) || FApp::IsBenchmarking() )
	{
		GEnableInnerException = false;
	}
#endif	

#if WINVER > 0x502	// Windows Error Reporting is not supported on Windows XP
	if (FParse::Param(CmdLine, TEXT("useautoreporter")))
#endif
	{
		GUseCrashReportClient = false;
	}

#if UE_BUILD_DEBUG
	if( true && !GAlwaysReportCrash )
#else
	if( FPlatformMisc::IsDebuggerPresent() && !GAlwaysReportCrash )
#endif
	{
		// Don't use exception handling when a debugger is attached to exactly trap the crash. This does NOT check
		// whether we are the first instance or not!
		ErrorLevel = GuardedMain( CmdLine, hInInstance, hPrevInstance, nCmdShow );
	}
	else
	{
		// Use structured exception handling to trap any crashes, walk the the stack and display a crash dialog box.
#if !PLATFORM_SEH_EXCEPTIONS_DISABLED
		__try
#endif
 		{
			GIsGuarded = 1;
			// Run the guarded code.
			ErrorLevel = GuardedMainWrapper( CmdLine, hInInstance, hPrevInstance, nCmdShow );
			GIsGuarded = 0;
		}
#if !PLATFORM_SEH_EXCEPTIONS_DISABLED
		__except( GEnableInnerException ? EXCEPTION_EXECUTE_HANDLER : ReportCrash( GetExceptionInformation( ) ) )
		{
#if !(UE_BUILD_SHIPPING && WITH_EDITOR)
			// Release the mutex in the error case to ensure subsequent runs don't find it.
			ReleaseNamedMutex();
#endif
			// Crashed.
			ErrorLevel = 1;
			GError->HandleError();
			LaunchStaticShutdownAfterError();
			FMallocCrash::Get().PrintPoolsUsage();
			FPlatformMisc::RequestExit( true );
		}
#endif
	}

	// Final shut down.
	FEngineLoop::AppExit();

#if !(UE_BUILD_SHIPPING && WITH_EDITOR)
	// Release the named mutex again now that we are done.
	ReleaseNamedMutex();
#endif

	

	// pause if we should
	if (GShouldPauseBeforeExit)
	{
		Sleep(INFINITE);
	}

	return ErrorLevel;
}

