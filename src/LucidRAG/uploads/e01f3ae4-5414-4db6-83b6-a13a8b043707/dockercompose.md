# Docker Compose

<datetime class="hidden">2024-07-30T13:30</datetime>
<!--category-- Docker -->

Docker Compose is a tool for defining and running multi-container Docker applications. With Compose, you use a YAML file to configure your application's services. Then, with a single command, you create and start all the services from your configuration.

At the moment I use Docker Compose to run a few services on my server.
- Mostlylucid - my blog (this one)
- Cloudflared - a service that tunnels traffic to my server
- Watchtower - a service that checks for updates to my containers and restarts them if necessary.

Here is the `docker-compose.yml` file I use to run these services:

```yaml 
services:
  mostlylucid:
    image: scottgal/mostlylucid:latest
    labels:
        - "com.centurylinklabs.watchtower.enable=true"
  cloudflared:
    image: cloudflare/cloudflared:latest
    command: tunnel --no-autoupdate run --token ${CLOUDFLARED_TOKEN}
    env_file:
      - .env
        
  watchtower:
    image: containrrr/watchtower
    container_name: watchtower
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      - WATCHTOWER_CLEANUP=true
      - WATCHTOWER_LABEL_ENABLE=true
    command: --interval 300 # Check for updates every 300 seconds (5 minutes)
```