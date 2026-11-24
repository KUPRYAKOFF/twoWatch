"use strict";
(() => {
  let microphone = null,
    muted = false;
  const peers = new Map();
  const button = document.getElementById("voice-join"),
    mute = document.getElementById("voice-mute"),
    status = document.getElementById("voice-status");
  function drop(id) {
    const peer = peers.get(id);
    if (peer) {
      peer.pc.close();
      peer.audio.remove();
      peers.delete(id);
    }
  }
  function stop() {
    for (const id of peers.keys()) drop(id);
    microphone?.getTracks().forEach((track) => track.stop());
    microphone = null;
    muted = false;
    button.textContent = "♬ Голосовой чат";
    mute.hidden = true;
    mute.textContent = "Микрофон включён";
    status.textContent = "";
  }
  function peerFor(id) {
    if (peers.has(id)) return peers.get(id);
    // Direct LAN/VPN peers: no public STUN/TURN service or cloud relay is needed.
    const pc = new RTCPeerConnection({ iceServers: [] });
    const audio = document.createElement("audio");
    audio.autoplay = true;
    document.getElementById("voice-audio").append(audio);
    const peer = { pc, audio, pending: [] };
    peers.set(id, peer);
    for (const track of microphone.getAudioTracks())
      pc.addTrack(track, microphone);
    pc.onicecandidate = (event) => {
      if (event.candidate && online)
        invoke(
          "VoiceSignal",
          id,
          JSON.stringify({ candidate: event.candidate.toJSON() }),
        ).catch((error) => {
          status.textContent = error.message;
        });
    };
    pc.ontrack = (event) => {
      audio.srcObject = event.streams[0];
      audio
        .play()
        .catch(() =>
          toast(
            "Разрешите воспроизведение звука, нажав кнопку голосового чата.",
          ),
        );
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === "connected")
        status.textContent = "Голос подключён";
      else if (pc.connectionState === "failed")
        status.textContent =
          "Нет голосовой связи. Проверьте, что устройства доступны по LAN или VPN.";
    };
    pc.onicegatheringstatechange = () => {
      if (
        pc.iceGatheringState === "complete" &&
        !pc.localDescription?.sdp.includes("a=candidate:")
      )
        status.textContent =
          "Браузер не предоставил адрес для голоса. Проверьте разрешения WebRTC.";
    };
    return peer;
  }
  async function update(people) {
    if (!microphone || !online) return;
    const others = people.filter(
      (person) => person.voiceConnected && person.id !== me.id,
    );
    for (const id of peers.keys())
      if (!others.some((person) => person.id === id)) drop(id);
    for (const person of others) {
      if (peers.has(person.id)) continue;
      const peer = peerFor(person.id);
      if (me.id < person.id) {
        await peer.pc.setLocalDescription(await peer.pc.createOffer());
        await invoke(
          "VoiceSignal",
          person.id,
          JSON.stringify({ description: peer.pc.localDescription }),
        );
      }
    }
    if (!others.length)
      status.textContent = "Вы в голосовом чате. Ждём друзей.";
  }
  async function signal(id, serialized) {
    if (
      !microphone ||
      !snapshot?.participants.some(
        (person) => person.id === id && person.voiceConnected,
      )
    )
      return;
    const data = JSON.parse(serialized),
      peer = peerFor(id);
    if (
      data.description &&
      ["offer", "answer"].includes(data.description.type)
    ) {
      await peer.pc.setRemoteDescription(data.description);
      for (const candidate of peer.pending.splice(0))
        await peer.pc.addIceCandidate(candidate);
      if (data.description.type === "offer") {
        await peer.pc.setLocalDescription(await peer.pc.createAnswer());
        await invoke(
          "VoiceSignal",
          id,
          JSON.stringify({ description: peer.pc.localDescription }),
        );
      }
    } else if (data.candidate) {
      if (peer.pc.remoteDescription)
        await peer.pc.addIceCandidate(data.candidate);
      else peer.pending.push(data.candidate);
    }
  }
  window.addEventListener("cinema-connected", (event) => {
    event.detail.on("VoiceSignal", (id, value) =>
      signal(id, value).catch((error) => {
        status.textContent = "Не удалось установить голосовую связь";
        console.error(error);
      }),
    );
    document.getElementById("voice-controls").hidden = false;
    document
      .querySelector(".social-footer")
      .before(document.getElementById("voice-controls"));
  });
  window.addEventListener("cinema-people", (event) =>
    update(event.detail).catch((error) => {
      status.textContent = error.message;
    }),
  );
  window.addEventListener("cinema-offline", stop);
  window.addEventListener("pagehide", stop);
  button.addEventListener("click", async () => {
    if (microphone) {
      stop();
      if (online)
        await invoke("Voice", false).catch((error) => toast(error.message));
      return;
    }
    if (!online) {
      toast("Сначала подключитесь к комнате.");
      return;
    }
    if (!navigator.mediaDevices?.getUserMedia) {
      toast(
        "Для микрофона браузеру нужна HTTPS-ссылка. Локально можно использовать localhost.",
      );
      return;
    }
    button.disabled = true;
    try {
      microphone = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true },
        video: false,
      });
      button.textContent = "Выйти из голосового чата";
      mute.hidden = false;
      status.textContent = "Подключаем голос…";
      await invoke("Voice", true);
      await update(snapshot.participants);
    } catch (error) {
      stop();
      toast("Не удалось включить микрофон: " + error.message);
    } finally {
      button.disabled = false;
    }
  });
  mute.addEventListener("click", () => {
    muted = !muted;
    microphone?.getAudioTracks().forEach((track) => (track.enabled = !muted));
    mute.textContent = muted ? "Микрофон выключен" : "Микрофон включён";
  });
})();
