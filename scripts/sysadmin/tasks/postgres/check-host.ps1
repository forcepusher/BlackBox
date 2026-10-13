# This PC, which the VM sees as 10.0.2.2, is in the app network, so it must reach PostgreSQL.
if (Test-LabPort 5432) { "PASS port 5432 answers from the app network" } else { "FAIL port 5432 answers from the app network" }
