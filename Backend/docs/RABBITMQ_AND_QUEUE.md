# RabbitMQ Queue and Pipeline Design

## Why the queue often shows 0 messages

- **Ready = 0, Unacked = 0, Total = 0** is normal when:
  - Messages are published and the consumer processes them quickly.
  - Flow: **Publish → Queue → Consumer receives → (processing) → Ack → Message removed.**

So “0 messages” usually means **everything is working**, not that nothing is happening.

## Why "Get message(s)" shows "Queue is empty" (localhost:15672)

On the queue page (e.g. `http://localhost:15672/#/queues/%2F/audio_queue`), **Get message(s)** only returns messages that are **Ready** (not yet delivered to a consumer).

- Once the backend consumes a message, it becomes **Unacked** and is no longer in the ready list, so **Get message(s)** has nothing to return and shows **Queue is empty**.
- So seeing "Queue is empty" when you click Get message(s) is **expected** when the consumer is running and has already taken the message (or when no message was published).

**How to confirm the pipeline is working:**
1. **Unacked count:** Speak and release the mic — you should see **Unacked = 1** for a few seconds, then 0 again.
2. **Backend logs:** `[WS] About to publish` and `RabbitMQ: message published to audio_queue` when you send voice.
3. **Test endpoint:** `POST http://localhost:5000/api/test/publish-rabbit` — message is consumed almost immediately; Get message(s) stays empty, but Unacked may briefly show 1 and pipeline logs appear.

## What we changed (to fix visibility and thread issues)

### 1. Queue visibility (Unacked while processing)

- **Before:** `autoAck: true` → message was removed as soon as the consumer received it, so the UI always showed 0.
- **Now:** **Manual ack** — we call `BasicAck` only after the full pipeline (Whisper → LLM → TTS → send) finishes.
- **Result:** While a request is being processed, RabbitMQ UI shows **Unacked = 1** (with prefetch 1). After the response is sent, the message is acked and removed.

### 2. Prefetch = 1

- We set `BasicQos(prefetchCount: 1)` so only **one message** is delivered at a time.
- So you see at most 1 Unacked message, and the consumer does not pull more work than it can handle.

### 3. Heavy work off the consumer thread (no thread pool starvation)

- **Before:** The RabbitMQ consumer callback did all work (Whisper, Ollama, Piper) and could block for a long time, leading to “heartbeat running longer than expected” and thread pool starvation.
- **Now:**
  - **Consumer callback** only: deserialize message, write WAV to temp file, push **(path, clientId, deliveryTag)** into an in-memory channel, then **return immediately** (no heavy work).
  - A **background processor loop** reads from that channel and runs: Whisper → embedding → memory → Ollama → TTS → WebSocket send.
  - A **dedicated ack loop** (single reader) calls `BasicAck(deliveryTag)` when the processor finishes. Only this loop touches `BasicAck` (IModel is not thread-safe).
- **Result:** The RabbitMQ consumer thread is no longer blocked by STT/LLM/TTS, so heartbeats and other work can run normally.

### 4. Publish rate in the UI

- Publish rate can still look like 0.00/s if you send rarely; the UI refresh may miss short spikes.
- To verify publishing: check backend logs for `[WS] About to publish` and `RabbitMQ: message published to audio_queue`, or use the test endpoint `POST /api/test/publish-rabbit`.

### 5. Debugging pipeline and queue

- **Unacked = 1** while you speak and wait for a reply → message is in the pipeline.
- **Ready + Unacked = 0** after the reply is sent → message was acked and removed.
- Logs: `[Pipeline] STT`, `[Pipeline] LLM`, `[Pipeline] TTS` show progress; ack happens after the pipeline completes.

### 6. Separation of concerns (current design)

- **Single process today:** WebSocket handler, RabbitMQ publish, consumer, and AI pipeline (Whisper, Ollama, Piper) all run in one backend.
- **Within that process we now have:**
  - **WebSocket thread:** Receives audio, publishes to RabbitMQ.
  - **Consumer thread:** Receives message, enqueues to in-memory channel, returns (no heavy work).
  - **Processor loop:** Does STT → LLM → TTS and sends reply.
  - **Ack loop:** Only calls `BasicAck` so the channel is used from one logical “owner” for acks.
- For **scalability** (multiple workers, separate services), you can later run the same consumer + processor in a separate worker service and scale it independently.

## Summary

| Issue | Change |
|--------|--------|
| Queue always 0 | Manual ack; message stays Unacked until pipeline finishes. |
| Publish rate 0.00/s | Normal for low traffic; verify with logs or `/api/test/publish-rabbit`. |
| Heavy work on consumer | Pipeline runs in a background processor; consumer only enqueues and returns. |
| Thread pool / heartbeat | Consumer no longer blocked; ack on a dedicated loop. |
| No queue visibility | Unacked = 1 while processing; prefetch 1. |
| Monolith | Still one process; internal separation (consumer vs processor vs ack) to avoid blocking and prepare for future split. |
