#!/bin/bash
cd /home/user/UnitySessionStreamer/Tests

# Find the reference assemblies
REF_DIR="/usr/lib/dotnet/shared/Microsoft.NETCore.App/8.0.0"

/usr/lib/dotnet/dotnet exec /usr/lib/dotnet/sdk/8.0.122/Roslyn/bincore/csc.dll \
    /target:exe \
    /out:StandaloneDownloadTest.exe \
    /langversion:latest \
    /reference:"$REF_DIR/System.Private.CoreLib.dll" \
    /reference:"$REF_DIR/System.Runtime.dll" \
    /reference:"$REF_DIR/System.Console.dll" \
    /reference:"$REF_DIR/System.Collections.dll" \
    /reference:"$REF_DIR/System.IO.FileSystem.dll" \
    /reference:"$REF_DIR/System.Security.Cryptography.dll" \
    StandaloneDownloadTest.cs 2>&1
