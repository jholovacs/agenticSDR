# Cross-platform entry points (Windows / Linux / macOS). Requires .NET SDK and Make.
.PHONY: run test station help

help:
	@echo Targets:
	@echo   make run     - start the receiver at http://127.0.0.1:5088
	@echo   make station - start the transmit desk at http://127.0.0.1:5090
	@echo   make test    - run the unit tests

run:
	dotnet run --project Host

station:
	dotnet run --project Station

test:
	dotnet build Host/AgenticSdr.Host.csproj -c Debug --nologo
	dotnet test Tests/AgenticSdr.Tests.csproj
