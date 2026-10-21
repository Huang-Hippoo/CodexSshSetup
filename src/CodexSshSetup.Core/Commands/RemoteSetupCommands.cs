using CodexSshSetup.Core.Models;

namespace CodexSshSetup.Core.Commands;

public static class RemoteSetupCommands
{
    public static CommandSpec EnsureUserProxy(ServerProfile profile)
    {
        const string script = """
            set -eu
            umask 077
            proxy=http://127.0.0.1:17890
            missing=0
            different=0
            for name in HTTP_PROXY HTTPS_PROXY http_proxy https_proxy; do
                eval 'value=${'"$name"'-}'
                if [ -z "$value" ]; then missing=1; elif [ "$value" != "$proxy" ]; then different=1; fi
            done
            if [ "$missing" = 0 ]; then
                if [ "$different" = 0 ]; then echo PROXY_CONFIG=unchanged; else echo PROXY_CONFIG=existing_different; fi
                exit 0
            fi
            root="$HOME/.config/codex-ssh-setup"
            mkdir -p "$root"
            exec 9>"$root/setup.lock"
            flock -w 60 9
            stamp=$(date -u +%Y%m%dT%H%M%SZ)-$$
            backup="$root/backups/$stamp"
            mkdir -p "$backup"
            target="$root/proxy.sh"
            if [ ! -f "$target" ]; then
                tmp=$(mktemp "$root/proxy.XXXXXX")
                cat >"$tmp" <<'PROXY_ENV'
            # Codex SSH Setup: fill missing user proxy variables only.
            [ -n "${HTTPS_PROXY:-}" ] || export HTTPS_PROXY=http://127.0.0.1:17890
            [ -n "${https_proxy:-}" ] || export https_proxy=http://127.0.0.1:17890
            [ -n "${http_proxy:-}" ] || export http_proxy=http://127.0.0.1:17890
            [ -n "${HTTP_PROXY:-}" ] || export HTTP_PROXY=http://127.0.0.1:17890
            PROXY_ENV
                mv "$tmp" "$target"
            fi
            shell=${SHELL:-/bin/sh}
            case "${shell##*/}" in
                bash|sh|dash|ksh)
                    login=.profile
                    if [ -f "$HOME/.bash_profile" ]; then login=.bash_profile;
                    elif [ -f "$HOME/.bash_login" ]; then login=.bash_login; fi
                    startup_files=".bashrc $login" ;;
                zsh) startup_files='.zshrc .zprofile' ;;
                *) echo 'Unsupported login shell for user proxy startup files' >&2; exit 2 ;;
            esac
            for name in $startup_files; do
                file="$HOME/$name"
                if [ -L "$file" ]; then echo "Refusing to replace symlink: $name" >&2; exit 2; fi
                if [ -f "$file" ] && grep -q '^# BEGIN CODEX SSH SETUP USER PROXY$' "$file"; then continue; fi
                tmp=$(mktemp "$HOME/.codex-proxy-startup.XXXXXX")
                cat >"$tmp" <<'PROXY_LOADER'
            # BEGIN CODEX SSH SETUP USER PROXY
            [ ! -r "$HOME/.config/codex-ssh-setup/proxy.sh" ] || . "$HOME/.config/codex-ssh-setup/proxy.sh"
            # END CODEX SSH SETUP USER PROXY
            PROXY_LOADER
                if [ -f "$file" ]; then
                    cp -p "$file" "$backup/$name"
                    cat "$file" >>"$tmp"
                    chmod --reference="$file" "$tmp"
                fi
                mv "$tmp" "$file"
            done
            . "$target"
            if [ "$different" = 0 ]; then echo PROXY_CONFIG=configured; else echo PROXY_CONFIG=existing_different; fi
            echo "PROXY_BACKUP=$backup"
            """;
        return SshCommandFactory.WithConnection(new CommandSpec("ssh.exe", ["-T", profile.Alias, "sh -s"],
            script, Timeout: TimeSpan.FromSeconds(90)), profile);
    }

    public static CommandSpec EnsureCodex(ServerProfile profile)
    {
        const string script = """
            set -eu
            umask 077
            root="$HOME/.local/codex-ssh-setup"
            mkdir -p "$root" "$HOME/.local/bin"
            exec 9>"$root/install.lock"
            flock -w 600 9
            export PATH="$root/node/bin:$root/npm/bin:$HOME/.local/bin:$PATH"
            proxy=http://127.0.0.1:17890
            export HTTP_PROXY="$proxy" HTTPS_PROXY="$proxy" http_proxy="$proxy" https_proxy="$proxy"
            target=$(command -v codex 2>/dev/null || true)
            if [ -z "$target" ]; then
                command -v npm >/dev/null || { echo CODEX_SETUP_NODE_REQUIRED >&2; exit 2; }
                echo 'CODEX_INSTALL=downloading'
                npm --proxy="$proxy" --https-proxy="$proxy" install --global --prefix "$root/npm" @openai/codex
                target="$root/npm/bin/codex"
            else
                echo 'CODEX_INSTALL=reused'
            fi
            wrapper="$HOME/.local/bin/codex-codex-ssh-setup"
            if [ ! -x "$wrapper" ]; then
                [ "$target" != "$wrapper" ] || { echo 'Codex wrapper target is recursive' >&2; exit 2; }
                encoded=$(printf '%s' "$target" | base64 | tr -d '\n')
                tmp=$(mktemp "$HOME/.local/bin/.codex-wrapper.XXXXXX")
                cat >"$tmp" <<'WRAPPER'
            #!/bin/sh
            root="$HOME/.local/codex-ssh-setup"
            export PATH="$root/node/bin:$root/npm/bin:$PATH"
            proxy=http://127.0.0.1:17890
            export HTTP_PROXY="$proxy" HTTPS_PROXY="$proxy" http_proxy="$proxy" https_proxy="$proxy"
            WRAPPER
                printf 'exec "$(printf %%s %s | base64 -d)" "$@"\n' "'$encoded'" >>"$tmp"
                chmod 700 "$tmp"
                mv "$tmp" "$wrapper"
                if [ ! -e "$HOME/.local/bin/codex" ] && [ ! -L "$HOME/.local/bin/codex" ]; then
                    ln -s "$wrapper" "$HOME/.local/bin/codex"
                fi
            fi
            "$wrapper" --version
            """;
        return SshCommandFactory.WithConnection(new CommandSpec("ssh.exe", ["-T", profile.Alias, "sh -s"],
            script, Timeout: TimeSpan.FromMinutes(12)), profile);
    }
}
