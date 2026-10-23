FROM node:22-bookworm-slim AS frontend
WORKDIR /src/web
RUN npm install -g pnpm@11.15.0
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Judge.Core/ src/Judge.Core/
COPY src/Judge.Server/ src/Judge.Server/
RUN dotnet publish src/Judge.Server/Judge.Server.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /out/ artifacts/server/
COPY --from=frontend /src/web/dist/ web/dist/
COPY toolkit/ toolkit/
COPY deploy/supabase-ca.crt deploy/supabase-ca.crt
ENV ACM_ROOT=/app ACM_DATA=/tmp/acm-data ACM_HOST=0.0.0.0 PORT=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "artifacts/server/Judge.Server.dll"]
