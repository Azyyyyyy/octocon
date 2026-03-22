ARG ELIXIR_VERSION=1.19.5
ARG OTP_VERSION=27.0
ARG DEBIAN_VERSION=trixie-20260112-slim

FROM "hexpm/elixir:${ELIXIR_VERSION}-erlang-${OTP_VERSION}-debian-${DEBIAN_VERSION}"

ENV MIX_BUILD_ROOT=/build
ENV MIX_DEPS_PATH=/deps
ENV HOME=/home/user

RUN mkdir /build && \
  chown nobody /build && \
  mkdir /deps && \
  chown nobody /deps && \
  mkdir /docker && \
  chown nobody /docker && \
  mkdir /home/user && \
  chown nobody /home/user 

ADD dev/docker /docker

RUN chmod +x /docker/entrypoint.sh /docker/command.sh && \
  chown nobody /docker/entrypoint.sh /docker/command.sh

RUN apt-get update -y && apt-get install -y build-essential git libstdc++6 openssl libncurses6 locales ca-certificates curl gnupg iproute2 iputils-ping jq \
    && apt-get clean && rm -f /var/lib/apt/lists/*_*

# Set the locale
RUN sed -i '/en_US.UTF-8/s/^# //g' /etc/locale.gen && locale-gen

USER nobody

# install hex + rebar
RUN mix local.hex --force && \
  mix local.rebar --force

ENV LANG="en_US.UTF-8"
ENV LANGUAGE="en_US:en"
ENV LC_ALL="en_US.UTF-8"

VOLUME /app
VOLUME /build
VOLUME /deps
WORKDIR /app

EXPOSE 4000

CMD ["/docker/command.sh"]
ENTRYPOINT ["/docker/entrypoint.sh"]
