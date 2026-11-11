cls
 
rem verifica file
dotnet run --project ../../src/LoadTest.Cli -- validate .\PrimoTest\primo-test.loadtest.yaml

rem esecuzione load test
dotnet run --project ../../src/LoadTest.Cli -- run .\PrimoTest\primo-test.loadtest.yaml --db ./results/primo-test.db

dotnet run --project ../../src/LoadTest.Cli -- report --db ./results/primo-test.db

dotnet run --project ../../src/LoadTest.Cli -- report --db ./results/primo-test.db --csv ./results/primo-test.csv