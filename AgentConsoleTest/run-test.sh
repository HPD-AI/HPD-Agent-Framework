#!/bin/bash
dotnet build -c Debug TestDocUploadSimple.cs 2>&1 | grep -E "error|warning" | head -5
dotnet run -c Debug TestDocUploadSimple.cs 2>&1
