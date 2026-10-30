#!/usr/bin/env bash
# Creates the two Cloud Build triggers. Prerequisite (one-time, needs a browser): connect GitHub to Cloud Build
# with a 2nd-gen connection and link this repository (see README, "GitHub + Cloud Build").
#
#   PROJECT_ID=... HOST=shipping.example.com SQL_CONNECTION=proj:asia-south2:shipping-pg \
#   CONNECTION=github-conn REPOSITORY=my-org-shipping-poc ./deploy/infra/create-trigger.sh
#
# If a flag is rejected by your gcloud version, run: gcloud builds triggers create github --help
set -euo pipefail
: "${PROJECT_ID:?}"; : "${HOST:?}"; : "${SQL_CONNECTION:?}"; : "${CONNECTION:?name of the Cloud Build GitHub connection}"; : "${REPOSITORY:?name of the linked repository resource}"
BUILD_REGION="${BUILD_REGION:-asia-south1}"     # region of the Cloud Build connection + triggers (NOT the GKE region)
DEPLOY_REGION="${DEPLOY_REGION:-asia-south2}"
BUILD_SA="${BUILD_SA:-cloudbuild-deployer}"
REPO_RESOURCE="projects/$PROJECT_ID/locations/$BUILD_REGION/connections/$CONNECTION/repositories/$REPOSITORY"
SA="projects/$PROJECT_ID/serviceAccounts/$BUILD_SA@$PROJECT_ID.iam.gserviceaccount.com"

# main -> test, build, push, deploy
gcloud builds triggers create github --project "$PROJECT_ID" --region "$BUILD_REGION" \
  --name shipping-deploy-main --repository "$REPO_RESOURCE" --branch-pattern '^main$' \
  --build-config cloudbuild.yaml --service-account "$SA" \
  --substitutions "_HOST=$HOST,_SQL_CONNECTION=$SQL_CONNECTION,_REGION=$DEPLOY_REGION,_PAYMENTS_PROVIDER=${PAYMENTS_PROVIDER:-fake}"

# pull requests -> test + compile images only
gcloud builds triggers create github --project "$PROJECT_ID" --region "$BUILD_REGION" \
  --name shipping-pr-check --repository "$REPO_RESOURCE" --pull-request-pattern '^main$' \
  --build-config cloudbuild-pr.yaml --service-account "$SA" --comment-control COMMENTS_ENABLED
