# Lycanthrope

This is a small web app written by Joe Odams. It's basically an online version of the Werewolf social deduction game, primarily for my portfolio.

## Architecure

A big part of the motivation of building this was to gain a bit more experience working with SignalR for live web client updates as well as showing that Redis can be the only persistence layer you need.

The stack is pretty simple:

- Redis stores the game state per "lobby" or session
- SignalR pushes the necessary updates to other players in the room
- Blazor renders the UI
- A tiny DigitalOcean VPS is serving it out behind Nginx


## Running Locally

You'll need the dotnet 8 SDK installed.

You will need redis running on `localhost:6379`. I use the normal redis docker image for this locally.

Then simply use `dotnet run` from the root of this directory. 

## Deployment

Lycanthrope is deployed as an ASP.NET Core app on a DigitalOcean VPS at `lycanthrope.joeodams.co.uk`

Initially I thought it would be simpler to dockerise everything and deploy, but that's a bit heavy on a small VPS, so I ran the app directly on the metal, including Redis. 

### Deployment setup

- Ubuntu VPS
- Nginx as the reverse proxy
- `systemd` runs the Blazor app 
- Redis for persistence
- HTTPS handled by Nginx / Certbot
- To actually deploy, I run `dotnet publish` in my dev direcotry, and `scp` it to the server


### Deployment instructions for when I forget

- Make the change
- `dotnet publish`
- `SSH` to the VPS
- On the VPS: `cd /var/www/lycanthrope/releases/`
- `mkdir {new-release-name}`
- Go back on the dev machine
- `scp -i {path-to-private-key} -r ./publish/* root@{droplet-ip}:/var/www/lycanthrope/releases/{new-release-name}/`
- `SSH` to the VPS
- `sudo chown -R lycanthrope:lycanthrope /var/www/lycanthrope/releases/{new-release-name}`
- `sudo ln -sfn /var/www/lycanthrope/releases/{new-release-name} /var/www/lycanthrope/current`
- `sudo systemctl restart lycanthrope`
- `sudo systemctl status lycanthrope`
- You're done!