#!/usr/bin/env bash
# Deletes everything the POC created (stops all billing). Irreversible.
set -uo pipefail
: "${PROJECT_ID:?}"
REGION="${REGION:-asia-south2}"; CLUSTER="${CLUSTER:-shipping-poc}"; SQL_INSTANCE="${SQL_INSTANCE:-shipping-pg}"
read -r -p "Delete GKE cluster, Cloud SQL, Pub/Sub, BigQuery dataset, secrets and registry in $PROJECT_ID? (type 'delete') " a
[ "$a" = "delete" ] || exit 1
gcloud config set project "$PROJECT_ID" >/dev/null
helm uninstall shipping -n shipping 2>/dev/null || true    # removes the load balancer first
gcloud container clusters delete "$CLUSTER" --region "$REGION" --quiet
gcloud sql instances delete "$SQL_INSTANCE" --quiet
for s in status-service-sub transaction-events-bq transaction-events-dlq-sub; do gcloud pubsub subscriptions delete "$s" --quiet; done
for t in transaction-events transaction-events-dlq; do gcloud pubsub topics delete "$t" --quiet; done
bq rm -r -f -d "$PROJECT_ID:analytics"
for s in db-password jwt-key jwt-encryption-key carrier-client-secret razorpay-key-id razorpay-key-secret razorpay-webhook-secret; do gcloud secrets delete "$s" --quiet; done
gcloud artifacts repositories delete shipping --location "$REGION" --quiet
gcloud compute addresses delete shipping-ip --global --quiet
for g in auth booking payment status; do gcloud iam service-accounts delete "shipping-$g@$PROJECT_ID.iam.gserviceaccount.com" --quiet; done
gcloud iam service-accounts delete "cloudbuild-deployer@$PROJECT_ID.iam.gserviceaccount.com" --quiet
