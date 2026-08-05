// Lexarbor standalone build, test, deploy, and smoke pipeline.

pipeline {
    agent any

    options {
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
        disableConcurrentBuilds()
    }

    environment {
        IMAGE_NAME = 'lexarbor:latest'
        REPORT_DIR = "${env.WORKSPACE}/reports"
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Preflight') {
            steps {
                sh '''
                    set -e
                    docker info --format 'Docker {{.ServerVersion}}'
                    dotnet --version
                    node --version
                    npm --version
                '''
            }
        }

        stage('Backend') {
            steps {
                sh '''
                    set -e
                    mkdir -p "$REPORT_DIR"
                    dotnet restore src/Lexarbor.sln
                    dotnet build src/Lexarbor.sln --configuration Release --no-restore
                    dotnet test src/Lexarbor.sln \
                        --configuration Release \
                        --no-build \
                        --logger 'trx;logfilename=lexarbor-tests.trx' \
                        --results-directory "$REPORT_DIR"
                '''
            }
        }

        stage('Frontend') {
            steps {
                dir('frontend') {
                    sh '''
                        set -e
                        npm ci
                        npm run test:types
                        npm run build
                    '''
                }
            }
        }

        stage('Build Image') {
            steps {
                sh 'docker build -f src/Host/Dockerfile -t "$IMAGE_NAME" .'
            }
        }

        stage('Deploy') {
            steps {
                sh 'LEXARBOR_IMAGE="$IMAGE_NAME" bash start.sh'
            }
        }

        stage('Smoke Test') {
            steps {
                sh '''
                    set -e
                    for i in $(seq 1 30); do
                        CODE=$(docker run --rm --network lexarbor-net curlimages/curl:latest \
                            -s -o /dev/null -w "%{http_code}" --max-time 3 \
                            http://lexarbor:5008/health 2>/dev/null || true)
                        if [ "$CODE" = "200" ]; then
                            echo "Lexarbor ready after ${i}s"
                            exit 0
                        fi
                        sleep 1
                    done
                    echo 'Lexarbor smoke test failed'
                    docker logs --tail 100 lexarbor || true
                    exit 1
                '''
            }
        }
    }

    post {
        always {
            archiveArtifacts artifacts: 'reports/**', allowEmptyArchive: true
        }
    }
}
