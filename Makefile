# Cross-platform entry points (Windows / Linux / macOS). Requires .NET SDK and Make.
.PHONY: run test help

help:
	@echo Targets:
	@echo   make run   - start the host at http://127.0.0.1:5088
	@echo   make test  - run the unit tests

run:
	dotnet run --project Host

test:
	dotnet test Tests/AgenticSdr.Tests.csproj
