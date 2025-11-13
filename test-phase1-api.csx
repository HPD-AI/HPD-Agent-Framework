#!/usr/bin/env dotnet-script

#r "HPD-Agent/bin/Debug/net9.0/HPD-Agent.dll"

using HPD_Agent.Skills;

// Run all Phase 1 API tests
Phase1ApiTest.RunAllTests();
