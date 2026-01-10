#!/bin/bash
# Compile C# file without project system
cd "$(dirname "$0")"

# Try to compile using dotnet's Roslyn compiler directly
cat > build.rsp << 'EOF'
/target:exe
/out:StandaloneDownloadTest.exe
/langversion:latest
StandaloneDownloadTest.cs
EOF

# Use dotnet to invoke the compiler
/usr/lib/dotnet/dotnet exec /usr/lib/dotnet/sdk/8.0.122/Roslyn/bincore/csc.dll @build.rsp 2>&1
