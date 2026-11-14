#!/bin/sh
# Entrypoint of the API image. Runs the app as the user PUID/PGID name, or as root when PUID is
# unset or 0.
#
# Root stays the default because existing installs have volumes (the DataProtection keys, the
# download staging dir, the destination library) that root created, and a NAS bind mount often
# belongs to one specific user. A non-root default would break their writes on upgrade. Set
# PUID/PGID to the owner of those directories to run without root; this script changes no owner,
# so every writable mount must already belong to that user.
set -eu

uid="${PUID:-}"
gid="${PGID:-}"

if [ -z "$uid" ] || [ "$uid" = "0" ]; then
    exec "$@"
fi

case "$uid$gid" in
    *[!0-9]*)
        echo "api-entrypoint: PUID and PGID must be numeric (got PUID='$uid' PGID='$gid')" >&2
        exit 64
        ;;
esac
[ -n "$gid" ] || gid="$uid"

# root's home is not writable for another user, and yt-dlp and deno keep their caches under HOME.
home=/tmp/musichoarder-home
mkdir -p "$home"
chown "$uid:$gid" "$home"
export HOME="$home"

exec setpriv --reuid="$uid" --regid="$gid" --clear-groups --no-new-privs -- "$@"
