# syntax=docker/dockerfile:1
FROM python:3.12-slim
LABEL org.opencontainers.image.source="https://github.com/BackSlasher/shamash" \
      org.opencontainers.image.title="shamash-home"
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 HOME_DATA_DIR=/data
WORKDIR /srv
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY app ./app
COPY templates ./templates
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
# Bind-mounted ./data on the host is usually root-owned: the entrypoint chowns it and drops to this user (setpriv is in util-linux).
RUN useradd --uid 10001 --no-create-home shamash && mkdir /data && chown shamash /data && chmod +x /usr/local/bin/entrypoint.sh
VOLUME /data
EXPOSE 8000 8001
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD python -c "import urllib.request,sys; sys.exit(0 if b'true' in urllib.request.urlopen('http://127.0.0.1:8000/api/health', timeout=4).read() else 1)"
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["python", "-m", "app.main"]
