# Runtime/Integration/MobileBridge

Nhận sessionId, manifestPath, grant, protocolVersion theo Docs; trả event/result/lỗi có sessionId, eventId, schema version và sequence. Mobile sở hữu API, durable queue và sync/retry; callback Unity không đồng nghĩa backend đã lưu. Không nhận access/refresh token dài hạn. Android-specific code phải có guard và adapter Editor khi triển khai.
