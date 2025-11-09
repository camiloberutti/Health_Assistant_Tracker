FROM python:3.11-slim
ENV DEBIAN_FRONTEND=noninteractive
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends git sqlite3 ca-certificates && apt-get clean && rm -rf /var/lib/apt/lists/*
# The build will copy a local GarminDB directory (if provided) or try to git clone during build.
# We'll attempt to clone during build (if network available)
RUN git clone https://github.com/tcgoetz/GarminDB.git /app/GarminDB
WORKDIR /app/GarminDB
RUN python -m venv .venv
ENV PATH="/app/GarminDB/.venv/bin:$PATH"
RUN pip install --upgrade pip
RUN pip install -r requirements.txt
COPY run_garmindb.sh /usr/local/bin/run_garmindb.sh
RUN chmod +x /usr/local/bin/run_garmindb.sh
VOLUME ["/data"]
ENV GARMIN_USERNAME=""
ENV GARMIN_PASSWORD=""
ENV GARMIN_OUTPUT_DB="/data/garmin.sqlite"
ENV GARMIN_FLAGS="--activities --import --analyze --latest"
CMD ["/usr/local/bin/run_garmindb.sh"]
