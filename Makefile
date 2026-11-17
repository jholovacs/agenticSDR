# Cross-platform entry points (Windows / Linux / macOS). Requires .NET SDK and Make.
.PHONY: run test cw help

help:
	@echo Targets:
	@echo   make run  - start the receiver at http://127.0.0.1:5088
	@echo   make cw   - start the CW mode desk at http://127.0.0.1:5091
	@echo   make test - run the unit tests
	@echo Each app has an Apps bar linking the others; run both processes to traverse.

run:
	dotnet run --project Host

cw:
	dotnet run --project Modes/Cw

test:
	dotnet test Tests/AgenticSdr.Tests.csproj
