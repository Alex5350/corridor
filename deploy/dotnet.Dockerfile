# Parameterized Dockerfile for every Corridor .NET service (oktasim, adfssim,
# legacy, portal). One file lives in deploy/ on purpose: each service keeps its
# own directory free of build files. Build args:
#   PROJECT_PATH  csproj path relative to the repo root (build context)
#   ASSEMBLY      assembly name without .dll (drives the entrypoint)
#   SERVICE_PORT  container port for EXPOSE (the app binds it via ASPNETCORE_URLS)
# Example:
#   docker build -f deploy/dotnet.Dockerfile --build-arg PROJECT_PATH=src/Corridor.OktaSim/Corridor.OktaSim.csproj \
#     --build-arg ASSEMBLY=Corridor.OktaSim --build-arg SERVICE_PORT=8080 -t corridor-oktasim .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT_PATH=src/Corridor.OktaSim/Corridor.OktaSim.csproj
WORKDIR /src

# Copy the repo (the root .dockerignore keeps bin/obj/node_modules out) and
# publish the requested project. Directory.Build.props and NuGet.config ride
# along because they sit at the context root.
COPY . .
RUN dotnet publish "${PROJECT_PATH}" -c Release -o /out --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG SERVICE_PORT=8080
ARG ASSEMBLY=Corridor.OktaSim

WORKDIR /app
COPY --from=build /out ./

# Shared demo assets the services resolve at runtime: signing material and the
# XACML policy set. compose points each service at these files with absolute
# paths (for example OktaSim__SigningKeyPem=/corridor/certs/...). The chmod
# normalizes the repo's 0600 key files so the non-root user can read them;
# this material is committed demo signing data, not real secrets.
COPY certs/ /corridor/certs/
COPY policies/ /corridor/policies/
RUN chmod -R a+rX /corridor

# Non-root runtime user, fixed uid 1001 so host mounts stay predictable.
RUN useradd --uid 1001 --system --no-create-home --shell /usr/sbin/nologin corridor
USER corridor

ENV DOTNET_ASSEMBLY=${ASSEMBLY}.dll
EXPOSE ${SERVICE_PORT}

# exec keeps dotnet as PID 1 so SIGTERM reaches it for clean shutdowns.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$DOTNET_ASSEMBLY\""]
