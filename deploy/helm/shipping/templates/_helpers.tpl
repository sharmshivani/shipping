{{- define "shipping.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: shipping
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ .root.Chart.Name }}-{{ .root.Chart.Version }}
{{- end }}

{{- define "shipping.selector" -}}
app.kubernetes.io/name: {{ .name }}
{{- end }}
