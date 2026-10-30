# Build context = repository root:  docker build -f src/Dashboard/Dockerfile .
FROM nginxinc/nginx-unprivileged:stable-alpine
COPY src/Dashboard/nginx.conf /etc/nginx/conf.d/default.conf
COPY src/Dashboard/wwwroot/ /usr/share/nginx/html/
EXPOSE 8080
