#!/usr/bin/env bash
# One-time, re-runnable GCP setup for the Shipping POC (Cloud Shell works well: gcloud, bq, openssl preinstalled).
#   export PROJECT_ID=my-project
#   ./deploy/infra/setup.sh
# Optional: RAZORPAY_KEY_ID, RAZORPAY_KEY_SECRET, RAZORPAY_WEBHOOK_SECRET (otherwise placeholders are stored).
set -euo pipefail

: "${PROJECT_ID:?Set PROJECT_ID first, e.g. export PROJECT_ID=my-project}"
REGION="${REGION:-asia-south2}"                 # Delhi (as in the architecture diagram)
CLUSTER="${CLUSTER:-shipping-poc}"
NAMESPACE="${NAMESPACE:-shipping}"
AR_REPO="${AR_REPO:-shipping}"
SQL_INSTANCE="${SQL_INSTANCE:-shipping-pg}"
DB_NAME="shipping"; DB_USER="app"
TOPIC="transaction-events"; DLQ_TOPIC="transaction-events-dlq"
SUB="status-service-sub"; DLQ_SUB="transaction-events-dlq-sub"; BQ_SUB="transaction-events-bq"
BQ_DATASET="analytics"; BQ_TABLE="transaction_events"
STATIC_IP_NAME="shipping-ip"
BUILD_SA="cloudbuild-deployer"

step() { printf '\n==> %s\n' "$*"; }
exists() { "$@" >/dev/null 2>&1; }

gcloud config set project "$PROJECT_ID" >/dev/null
PROJECT_NUMBER="$(gcloud projects describe "$PROJECT_ID" --format='value(projectNumber)')"

step "Enabling APIs"
gcloud services enable \
  container.googleapis.com sqladmin.googleapis.com secretmanager.googleapis.com pubsub.googleapis.com \
  bigquery.googleapis.com artifactregistry.googleapis.com cloudbuild.googleapis.com compute.googleapis.com \
  iam.googleapis.com

step "Artifact Registry repo: $AR_REPO"
exists gcloud artifacts repositories describe "$AR_REPO" --location "$REGION" || \
  gcloud artifacts repositories create "$AR_REPO" --repository-format=docker --location "$REGION" \
    --description="Shipping POC images"

step "GKE Autopilot cluster: $CLUSTER (takes ~5-10 min the first time)"
exists gcloud container clusters describe "$CLUSTER" --region "$REGION" || \
  gcloud container clusters create-auto "$CLUSTER" --region "$REGION"

step "Cloud SQL for PostgreSQL: $SQL_INSTANCE (takes ~5-10 min the first time)"
exists gcloud sql instances describe "$SQL_INSTANCE" || \
  gcloud sql instances create "$SQL_INSTANCE" --database-version=POSTGRES_16 --edition=ENTERPRISE \
    --tier=db-g1-small --region "$REGION" --storage-size=10GB --storage-auto-increase --backup-start-time=21:00
SQL_CONNECTION="$(gcloud sql instances describe "$SQL_INSTANCE" --format='value(connectionName)')"
exists gcloud sql databases describe "$DB_NAME" --instance "$SQL_INSTANCE" || \
  gcloud sql databases create "$DB_NAME" --instance "$SQL_INSTANCE"

# ---- secrets (Secret Manager). Existing secrets are never overwritten. ----
ensure_secret() {   # name, value
  if exists gcloud secrets describe "$1"; then
    echo "   secret $1 exists, leaving as is"
  else
    printf '%s' "$2" | gcloud secrets create "$1" --replication-policy=user-managed --locations="$REGION" --data-file=- >/dev/null
    echo "   created secret $1"
  fi
}

step "Secrets"
if exists gcloud secrets describe db-password; then
  DB_PASSWORD="$(gcloud secrets versions access latest --secret=db-password)"
else
  DB_PASSWORD="$(openssl rand -hex 16)"
fi
ensure_secret db-password "$DB_PASSWORD"
gcloud sql users create "$DB_USER" --instance "$SQL_INSTANCE" --password "$DB_PASSWORD" >/dev/null 2>&1 || \
  gcloud sql users set-password "$DB_USER" --instance "$SQL_INSTANCE" --password "$DB_PASSWORD" >/dev/null

ensure_secret jwt-key "$(openssl genrsa 2048 2>/dev/null)"            # RSA private key: signs access/id tokens
ensure_secret jwt-encryption-key "$(openssl rand -base64 32)"          # 256-bit: encrypts auth codes / refresh tokens
ensure_secret carrier-client-secret "$(openssl rand -hex 24)"          # OAuth client secret for the carrier simulator
ensure_secret razorpay-key-id "${RAZORPAY_KEY_ID:-unset}"
ensure_secret razorpay-key-secret "${RAZORPAY_KEY_SECRET:-unset}"
ensure_secret razorpay-webhook-secret "${RAZORPAY_WEBHOOK_SECRET:-unset}"

step "Pub/Sub: topic, dead-letter topic, subscriptions"
exists gcloud services identity create --service=pubsub.googleapis.com || true
PUBSUB_SA="service-${PROJECT_NUMBER}@gcp-sa-pubsub.iam.gserviceaccount.com"
exists gcloud pubsub topics describe "$TOPIC" || gcloud pubsub topics create "$TOPIC"
exists gcloud pubsub topics describe "$DLQ_TOPIC" || gcloud pubsub topics create "$DLQ_TOPIC"
exists gcloud pubsub subscriptions describe "$DLQ_SUB" || \
  gcloud pubsub subscriptions create "$DLQ_SUB" --topic "$DLQ_TOPIC" --message-retention-duration=7d
exists gcloud pubsub subscriptions describe "$SUB" || \
  gcloud pubsub subscriptions create "$SUB" --topic "$TOPIC" --ack-deadline=30 \
    --dead-letter-topic "$DLQ_TOPIC" --max-delivery-attempts=5 \
    --min-retry-delay=10s --max-retry-delay=300s
# Pub/Sub itself must be allowed to move failed messages into the DLQ
gcloud pubsub topics add-iam-policy-binding "$DLQ_TOPIC" \
  --member="serviceAccount:$PUBSUB_SA" --role=roles/pubsub.publisher >/dev/null
gcloud pubsub subscriptions add-iam-policy-binding "$SUB" \
  --member="serviceAccount:$PUBSUB_SA" --role=roles/pubsub.subscriber >/dev/null

step "BigQuery: raw event table fed by a BigQuery subscription"
exists bq show "$PROJECT_ID:$BQ_DATASET" || bq --location="$REGION" mk -d --description "Raw transaction events" "$PROJECT_ID:$BQ_DATASET"
exists bq show "$PROJECT_ID:$BQ_DATASET.$BQ_TABLE" || \
  bq mk --table "$PROJECT_ID:$BQ_DATASET.$BQ_TABLE" \
    subscription_name:STRING,message_id:STRING,publish_time:TIMESTAMP,data:JSON,attributes:JSON
for role in roles/bigquery.dataEditor roles/bigquery.metadataViewer; do
  gcloud projects add-iam-policy-binding "$PROJECT_ID" --member="serviceAccount:$PUBSUB_SA" --role="$role" --condition=None >/dev/null
done
exists gcloud pubsub subscriptions describe "$BQ_SUB" || \
  gcloud pubsub subscriptions create "$BQ_SUB" --topic "$TOPIC" \
    --bigquery-table="$PROJECT_ID:$BQ_DATASET.$BQ_TABLE" --write-metadata

step "Workload Identity: one Google service account per microservice, least privilege"
# Each Kubernetes ServiceAccount (same name as the service, in namespace $NAMESPACE) may act as its Google SA.
make_gsa() {   # gsa-name  ksa-name
  exists gcloud iam service-accounts describe "$1@$PROJECT_ID.iam.gserviceaccount.com" || {
    gcloud iam service-accounts create "$1" --display-name "Shipping $2"
    sleep 10   # IAM is eventually consistent
  }
  gcloud iam service-accounts add-iam-policy-binding "$1@$PROJECT_ID.iam.gserviceaccount.com" \
    --role roles/iam.workloadIdentityUser \
    --member "serviceAccount:$PROJECT_ID.svc.id.goog[$NAMESPACE/$2]" >/dev/null
  gcloud projects add-iam-policy-binding "$PROJECT_ID" \
    --member "serviceAccount:$1@$PROJECT_ID.iam.gserviceaccount.com" --role roles/cloudsql.client --condition=None >/dev/null
}
grant_secret() {   # gsa-name  secret-id...
  local gsa="$1"; shift
  for s in "$@"; do
    gcloud secrets add-iam-policy-binding "$s" --member "serviceAccount:$gsa@$PROJECT_ID.iam.gserviceaccount.com" \
      --role roles/secretmanager.secretAccessor >/dev/null
  done
}

make_gsa shipping-auth auth-service
grant_secret shipping-auth db-password jwt-key jwt-encryption-key carrier-client-secret

make_gsa shipping-booking booking-service
grant_secret shipping-booking db-password
gcloud pubsub topics add-iam-policy-binding "$TOPIC" --role roles/pubsub.publisher \
  --member "serviceAccount:shipping-booking@$PROJECT_ID.iam.gserviceaccount.com" >/dev/null

make_gsa shipping-payment payment-service
grant_secret shipping-payment db-password razorpay-key-id razorpay-key-secret razorpay-webhook-secret
gcloud pubsub topics add-iam-policy-binding "$TOPIC" --role roles/pubsub.publisher \
  --member "serviceAccount:shipping-payment@$PROJECT_ID.iam.gserviceaccount.com" >/dev/null

make_gsa shipping-status status-service
grant_secret shipping-status db-password
gcloud pubsub subscriptions add-iam-policy-binding "$SUB" --role roles/pubsub.subscriber \
  --member "serviceAccount:shipping-status@$PROJECT_ID.iam.gserviceaccount.com" >/dev/null

step "Global static IP for the load balancer"
exists gcloud compute addresses describe "$STATIC_IP_NAME" --global || gcloud compute addresses create "$STATIC_IP_NAME" --global
STATIC_IP="$(gcloud compute addresses describe "$STATIC_IP_NAME" --global --format='value(address)')"

step "Cloud Build service account (used by the GitHub trigger)"
exists gcloud iam service-accounts describe "$BUILD_SA@$PROJECT_ID.iam.gserviceaccount.com" || {
  gcloud iam service-accounts create "$BUILD_SA" --display-name "Cloud Build deployer"
  sleep 10
}
for role in roles/artifactregistry.writer roles/container.developer roles/logging.logWriter; do
  gcloud projects add-iam-policy-binding "$PROJECT_ID" \
    --member "serviceAccount:$BUILD_SA@$PROJECT_ID.iam.gserviceaccount.com" --role "$role" --condition=None >/dev/null
done

step "Kubernetes namespace"
if command -v kubectl >/dev/null 2>&1; then
  gcloud container clusters get-credentials "$CLUSTER" --region "$REGION"
  kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -
else
  echo "   kubectl not found; Helm will create the namespace on first deploy."
fi

cat <<DONE

==========================================================================
 Setup complete.

 1. Create a DNS A record:   <your-host>  ->  $STATIC_IP
 2. Connect GitHub to Cloud Build (README, step "GitHub + Cloud Build"), then create the trigger with:
      PROJECT_ID=$PROJECT_ID HOST=<your-host> SQL_CONNECTION=$SQL_CONNECTION \\
        ./deploy/infra/create-trigger.sh
 3. Push to main. First deploy uses the fake payment gateway.

 Cloud SQL connection name: $SQL_CONNECTION
 Static IP:                 $STATIC_IP
==========================================================================
DONE
