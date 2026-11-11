#include <nexa/guest.h>
#include <nexa/abi100_ids.h>

static nx_u8 input[65536], output[65536], argument[4096];
static int awaiting_registration;
NX_EXPORT("nexa_alloc") nx_u32 nexa_alloc(nx_u32 size)
{
    nx_require(size <= sizeof input);
    return (nx_u32)(__UINTPTR_TYPE__)input;
}
NX_EXPORT("nexa_resume") nx_u64 nexa_resume(nx_u32 address, nx_u32 size)
{
    nx_require(address == (nx_u32)(__UINTPTR_TYPE__)input && size <= sizeof input);
    nx_value message = nx_decode(input, size);
    nx_writer reply = {output, 0, sizeof output};
    if (awaiting_registration) {
        nx_require(nx_is_string(nx_field(message, "kind"), "reply"));
        awaiting_registration = 0;
        nx_complete(&reply, nx_is_true(nx_field(message, "success")));
    } else if (nx_is_string(nx_field(message, "phase"), "Initialize")) {
        nx_writer args = {argument, 0, sizeof argument};
        nx_map(&args, 5);
        nx_key(&args, "body"); nx_string(&args, "Hello from the C WebAssembly SDK");
        nx_key(&args, "id"); nx_string(&args, "example.wasm:hello");
        nx_key(&args, "references"); nx_array(&args, 0);
        nx_key(&args, "registry"); nx_string(&args, "example.wasm:messages");
        nx_key(&args, "schema"); nx_map(&args, 3);
        nx_key(&args, "digest"); nx_string(&args, "example-string-v1");
        nx_key(&args, "id"); nx_string(&args, "example.wasm:message");
        nx_key(&args, "version"); nx_unsigned(&args, 1);
        nx_value value = {argument, args.size};
        nx_invoke(&reply, NEXA_DEFINITIONS_CONTRACT, NEXA_DEFINITIONS_REGISTER, "hello-definition", value);
        awaiting_registration = 1;
    } else nx_complete(&reply, 1);
    return nx_result(&reply);
}
