# Status page

A tiny status page. It listens on port 8080 and keeps the checks it records in /data/checks.log.

- `GET /` shows how many checks are recorded.
- `POST /checks` records one; the request body is a short note.

Build and try it:

    docker build -t statuspage .
    docker run --rm -p 8080:8080 statuspage
