#!/bin/bash
echo "Testing document upload..."
dotnet run 2>&1 | head -30 &
PID=$!
sleep 3
kill $PID 2>/dev/null
wait $PID 2>/dev/null

echo ""
echo "Checking uploaded files..."
ls -la agent-skills/skill-documents/content/
ls -la agent-skills/skill-documents/metadata/
