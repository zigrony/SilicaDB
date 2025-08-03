## 🔌 Protocol Implementations

All implementations expose raw `byte[]` payload contracts — no JSON, XML, or textual formats. Serialization and deserialization are handled via pluggable strategy components.

### 🌐 HTTP Interface *(default)*

```csharp
public class HttpInterfaceFactory : IDatabaseInterfaceFactory
{
    public IDatabaseInterface Create(InterfaceConfiguration config)
    {
        return new HttpInterface(config);
    }
}
```

- Accepts and responds with raw `byte[]` payloads
- Payload format is determined by `IRequestPayloadSerializer`
- No dependency on HTTP headers or JSON conventions
- Request body and response framing are binary-defined

### 🔁 gRPC Interface *(planned)*

- Native binary support via protobuf, directly compatible with `byte[]` buffers
- Supports streaming and unary requests using raw bytes
- Serializer module aligns with core interface contract

### 🧩 Payload Serializer Interface

```csharp
public interface IRequestPayloadSerializer
{
    TRequest Deserialize<TRequest>(byte[] payload);
    byte[] Serialize<TResponse>(TResponse response);
}
```

- Implementations could include: custom binary protocol, protobuf, flatbuffers, or hand-crafted layouts
- Allows different protocol layers to reuse logic
- Completely decoupled from transport mechanics

---

This removes any assumption of human-readable payloads and future-proofs the interface layer for high-efficiency environments, embedded scenarios, and cross-platform integrations.

Want me to mock out a basic `IRequestPayloadSerializer` implementation — say, one using a custom layout with magic headers, op codes, and field framing for something like query submission? Or do you want to define the binary protocol spec first and scaffold from there?