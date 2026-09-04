# =============================================================================
# Backup image.
#
# `age` is installed at build time, not at run time: the backup service sits on
# the internal network with no route to the internet, which is deliberate — the
# thing holding a copy of every user's data should not be able to reach out.
# =============================================================================

FROM postgres:18-alpine

# age is in Alpine's community repository.
RUN apk add --no-cache age

COPY backup/backup.sh /usr/local/bin/backup.sh
RUN chmod +x /usr/local/bin/backup.sh

ENTRYPOINT ["/bin/sh", "/usr/local/bin/backup.sh"]
