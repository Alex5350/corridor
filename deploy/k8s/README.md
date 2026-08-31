# Kubernetes / OpenShift skeleton

Same stance as the AWS CloudFormation template in ../aws: an honest skeleton, never
applied by CI, documenting the deployment shape rather than pretending to a managed
cluster. Apply with kubectl after publishing the images:

    docker build -f deploy/dotnet.Dockerfile --build-arg PROJECT_PATH=src/Corridor.OktaSim -t corridor-oktasim:local .
    # ...likewise for adfssim, legacy, portal; spa builds from deploy/spa.Dockerfile
    kubectl apply -f deploy/k8s/

## What is here

- namespace.yaml: a plain namespace.
- configmap.yaml: every URL the services dial each other by, swapped from localhost to
  service DNS (oktasim.corridor.svc.cluster.local etc.). The one deliberate exception is
  the OIDC issuer the BROWSER sees: browsers resolve localhost, not cluster DNS, so
  OktaSim:Issuer stays a host-reachable URL in the ConfigMap and the note in corridor-ecs
  applies here too (put the cluster behind one DNS name, or port-forward).
- secrets.yaml: placeholder stringData for the named demo constants (portal client
  secret, legacy client secret, SCIM token, SQL password). Values are the documented
  demo constants, not real secrets; replace with sealed secrets or a vault in any real
  use.
- apps.yaml: one Deployment + Service per image (oktasim, adfssim, legacy, portal, spa),
  non-root (the images already run as uid 1001), liveness/readiness probes on /healthz
  (the SPA probes /), single replica each, resource requests left modest.
- ingress.yaml: one Ingress routing / to the portal, /api to the portal, and host-based
  or path-based rules for the spa; for OpenShift, apply route.yaml instead (a Route
  exposing the portal service; OpenShift builds the TLS edge for you).

## Deliberate omissions

- No database workload: bring your own SQL Server (RDS, on-prem over VPN), matching the
  ECS template's bring-your-own stance.
- No HPA, PDB, or network policies: three replicas of a demo would be theater.
- No Helm chart: five flat manifests read faster than a values matrix for a skeleton.
