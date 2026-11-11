#ifndef NEXA_GUEST_H
#define NEXA_GUEST_H
/* Freestanding C11 guest SDK. All offsets/lengths are bounded by guest memory.
 * Values are borrowed until the next nexa_resume call. No WASI or libc needed. */
typedef unsigned char nx_u8;
typedef unsigned int nx_u32;
typedef unsigned long long nx_u64;
typedef struct { const nx_u8 *data; nx_u32 size; } nx_value;
typedef struct { nx_u8 *data; nx_u32 size, capacity; } nx_writer;
typedef struct { const nx_u8 *data; nx_u32 size, offset, nodes; } nx_reader;
#define NX_EXPORT(name) __attribute__((export_name(name)))
#define NX_MAX_BYTES (4u * 1024u * 1024u)

static inline void nx_require(int ok) { if (!ok) __builtin_trap(); }
static inline nx_u32 nx_strlen(const char *s) { nx_u32 n = 0; while (s[n]) { nx_require(n < 65536); n++; } return n; }
static inline void nx_reserve(nx_writer *w, nx_u32 count) { nx_require(w->size <= w->capacity && count <= w->capacity - w->size && w->capacity <= NX_MAX_BYTES); }
static inline void nx_byte(nx_writer *w, nx_u8 n) { nx_reserve(w, 1); w->data[w->size++] = n; }
static inline void nx_word(nx_writer *w, nx_u32 n) { for (nx_u32 i = 0; i < 4; i++) nx_byte(w, (nx_u8)(n >> (8 * i))); }
static inline void nx_copy(nx_writer *w, const nx_u8 *data, nx_u32 size) { nx_reserve(w, size); for (nx_u32 i = 0; i < size; i++) w->data[w->size++] = data[i]; }
static inline void nx_key(nx_writer *w, const char *s) { nx_u32 size = nx_strlen(s); nx_word(w, size); nx_copy(w, (const nx_u8 *)s, size); }
static inline void nx_string(nx_writer *w, const char *s) { nx_byte(w, 5); nx_key(w, s); }
static inline void nx_null(nx_writer *w) { nx_byte(w, 0); }
static inline void nx_boolean(nx_writer *w, int value) { nx_byte(w, 1); nx_byte(w, value ? 1 : 0); }
static inline void nx_unsigned(nx_writer *w, nx_u64 value) { nx_byte(w, 3); for (nx_u32 i = 0; i < 8; i++) nx_byte(w, (nx_u8)(value >> (8 * i))); }
static inline void nx_array(nx_writer *w, nx_u32 count) { nx_require(count <= 16384); nx_byte(w, 7); nx_word(w, count); }
static inline void nx_map(nx_writer *w, nx_u32 count) { nx_require(count <= 16384); nx_byte(w, 8); nx_word(w, count); }
static inline void nx_start(nx_writer *w) { w->size = 0; nx_word(w, 1); }
static inline nx_u8 nx_take(nx_reader *r) { nx_require(r->offset < r->size); return r->data[r->offset++]; }
static inline nx_u32 nx_read_word(nx_reader *r) { nx_u32 n = 0; for (nx_u32 i = 0; i < 4; i++) n |= (nx_u32)nx_take(r) << (8 * i); return n; }
static inline void nx_advance(nx_reader *r, nx_u32 count) { nx_require(r->offset <= r->size && count <= r->size - r->offset); r->offset += count; }
static inline void nx_skip(nx_reader *r, nx_u32 depth)
{
    nx_require(depth <= 32 && ++r->nodes <= 16384);
    nx_u8 tag = nx_take(r);
    switch (tag) {
    case 0: return;
    case 1: nx_require(nx_take(r) <= 1); return;
    case 2: case 3: case 4: nx_advance(r, 8); return;
    case 5: case 6: { nx_u32 n = nx_read_word(r); nx_advance(r, n); return; }
    case 7: case 8: {
        nx_u32 n = nx_read_word(r); nx_require(n <= 16384 - r->nodes);
        for (nx_u32 i = 0; i < n; i++) {
            if (tag == 8) { nx_u32 length = nx_read_word(r); nx_advance(r, length); }
            nx_skip(r, depth + 1);
        }
        return;
    }
    default: __builtin_trap();
    }
}
static inline nx_value nx_decode(const nx_u8 *data, nx_u32 size)
{
    nx_require(size <= NX_MAX_BYTES); nx_reader r = {data, size, 0, 0};
    nx_require(nx_read_word(&r) == 1); nx_skip(&r, 0); nx_require(r.offset == size);
    nx_value v = {data + 4, size - 4}; return v;
}
static inline int nx_bytes_equal(const nx_u8 *a, nx_u32 count, const char *b)
{
    if (count != nx_strlen(b)) return 0;
    for (nx_u32 i = 0; i < count; i++) if (a[i] != (nx_u8)b[i]) return 0;
    return 1;
}
static inline nx_value nx_field(nx_value map, const char *key)
{
    nx_reader r = {map.data, map.size, 0, 0}; nx_require(nx_take(&r) == 8);
    nx_u32 count = nx_read_word(&r); nx_require(count <= 16384);
    for (nx_u32 i = 0; i < count; i++) {
        nx_u32 size = nx_read_word(&r), offset = r.offset; nx_advance(&r, size);
        int match = nx_bytes_equal(r.data + offset, size, key); offset = r.offset;
        nx_skip(&r, 1);
        if (match) { nx_value v = {r.data + offset, r.offset - offset}; return v; }
    }
    nx_value missing = {0, 0}; return missing;
}
static inline int nx_is_string(nx_value value, const char *text)
{
    if (!value.size || value.data[0] != 5) return 0;
    nx_reader r = {value.data, value.size, 1, 0}; nx_u32 size = nx_read_word(&r);
    nx_u32 offset = r.offset; nx_advance(&r, size);
    return r.offset == r.size && nx_bytes_equal(r.data + offset, size, text);
}
static inline int nx_is_true(nx_value value) { return value.size == 2 && value.data[0] == 1 && value.data[1] == 1; }
static inline void nx_raw(nx_writer *w, nx_value value)
{
    nx_reader r = {value.data, value.size, 0, 0}; nx_skip(&r, 0); nx_require(r.offset == r.size);
    nx_copy(w, value.data, value.size);
}
static inline void nx_invoke(nx_writer *w, const char *contract, nx_u32 method, const char *idempotency_key, nx_value arguments)
{
    nx_start(w); nx_map(w, 5);
    nx_key(w, "arguments"); nx_raw(w, arguments);
    nx_key(w, "contract"); nx_string(w, contract);
    nx_key(w, "idempotencyKey"); if (idempotency_key) nx_string(w, idempotency_key); else nx_null(w);
    nx_key(w, "kind"); nx_string(w, "invoke");
    nx_key(w, "method"); nx_unsigned(w, method);
}
static inline void nx_complete(nx_writer *w, int success)
{
    nx_start(w); nx_map(w, 2);
    nx_key(w, "kind"); nx_string(w, "complete");
    nx_key(w, "value"); nx_map(w, 1); nx_key(w, "success"); nx_boolean(w, success);
}
static inline nx_u64 nx_result(nx_writer *w) { return ((nx_u64)(nx_u32)(__UINTPTR_TYPE__)w->data << 32) | w->size; }
#endif
