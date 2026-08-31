// Declarative Jenkins pipeline mirroring .github/workflows/ci.yml
// (stages Restore, Build, Test). Kept in parity with the GitHub workflow;
// this repo runs on GitHub Actions, the Jenkinsfile exists for parity.
// Tool assumptions, adjust labels to your controller:
//   tools { nodejs "node22" } for the SPA build (optional stage, commented)
//   dotnet 10 SDK on PATH or a tool named net10 via the "dotnet" tool label.
// Parity note: this pipeline runs restore, build, and unit tests. The GitHub
// workflow additionally runs spa, artifacts, api-regression (newman), and e2e
// jobs; this repo's canonical pipeline is .github/workflows/ci.yml.
pipeline {
    agent any

    options {
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
    }

    environment {
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
    }

    stages {
        stage('Restore') {
            steps {
                sh 'dotnet restore Corridor.slnx'
            }
        }
        stage('Build') {
            steps {
                sh 'dotnet build Corridor.slnx --no-restore'
            }
        }
        stage('Test') {
            steps {
                // Same exclusion as ci.yml: the integration suite needs the
                // compose db stack and runs separately, unit tests stay
                // container free.
                sh 'dotnet test Corridor.slnx --no-build --filter "FullyQualifiedName!~IntegrationTests" --logger "junit;LogFileName=test-results.xml"'
            }
        }
    }

    post {
        always {
            // JUnit XML from the dotnet logger above; empty results keep the
            // build green before any test project produces output.
            junit allowEmptyResults: true, testResults: '**/TestResults/**/test-results.xml'
        }
    }
}
