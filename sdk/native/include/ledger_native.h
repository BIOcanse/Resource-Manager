#ifndef RESOURCE_MANAGER_LEDGER_NATIVE_H
#define RESOURCE_MANAGER_LEDGER_NATIVE_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(RM_LEDGER_BUILD)
#    define RM_LEDGER_API __declspec(dllexport)
#  else
#    define RM_LEDGER_API __declspec(dllimport)
#  endif
#  define RM_LEDGER_CALL __cdecl
#else
#  define RM_LEDGER_API
#  define RM_LEDGER_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define RM_LEDGER_ABI_VERSION 1u
#define RM_LEDGER_CANDIDATE_C2_CHAIN 0u
#define RM_LEDGER_MAX_SUBSCRIBERS_PER_TABLE 64u

/* Status values 0..17 and 255 preserve the Native comparison vocabulary.
   18/19 distinguish a new partition shell from an existing shell. */
typedef int32_t rm_ledger_status;
#define RM_LEDGER_OK                         ((rm_ledger_status)0)
#define RM_LEDGER_IN_MEMORY                  ((rm_ledger_status)1)
#define RM_LEDGER_NEWLY_ALLOCATED            ((rm_ledger_status)2)
#define RM_LEDGER_NOT_IN_MEMORY              ((rm_ledger_status)3)
#define RM_LEDGER_LOADING                    ((rm_ledger_status)4)
#define RM_LEDGER_COMPLETED                  ((rm_ledger_status)5)
#define RM_LEDGER_FORCE_RELEASED             ((rm_ledger_status)6)
#define RM_LEDGER_UPDATED                    ((rm_ledger_status)7)
#define RM_LEDGER_REGISTRATION_ONLY          ((rm_ledger_status)8)
#define RM_LEDGER_IN_USE                     ((rm_ledger_status)9)
#define RM_LEDGER_BLOCKED                    ((rm_ledger_status)10)
#define RM_LEDGER_NONEXISTENT                ((rm_ledger_status)11)
#define RM_LEDGER_INPUT_ERROR                ((rm_ledger_status)12)
#define RM_LEDGER_ALLOCATION_FAILED          ((rm_ledger_status)13)
#define RM_LEDGER_SPECIFICATION_LIMIT        ((rm_ledger_status)14)
#define RM_LEDGER_CHANGED_BEYOND_TOLERANCE   ((rm_ledger_status)15)
#define RM_LEDGER_UNSUPPORTED_PLATFORM       ((rm_ledger_status)16)
#define RM_LEDGER_INVALID_STATE              ((rm_ledger_status)17)
#define RM_LEDGER_PARTITION_CREATED          ((rm_ledger_status)18)
#define RM_LEDGER_PARTITION_EXISTS           ((rm_ledger_status)19)
#define RM_LEDGER_INTERNAL_ERROR             ((rm_ledger_status)255)

#define RM_LEDGER_TARGET_TABLE      0u
#define RM_LEDGER_TARGET_PARTITION  1u
#define RM_LEDGER_MODE_UNRESTRICTED 0u
#define RM_LEDGER_MODE_NORMAL       1u
#define RM_LEDGER_MODE_OPTIMIZE     2u
#define RM_LEDGER_END_COMPLETE      0u
#define RM_LEDGER_END_DISCARD       1u
#define RM_LEDGER_RECOVERABILITY_NOT_RECOVERABLE 0u
#define RM_LEDGER_RECOVERABILITY_RECOVERABLE     1u
#define RM_LEDGER_IMPACT_FATAL          0u
#define RM_LEDGER_IMPACT_OBSERVABLE_NOW 1u
#define RM_LEDGER_IMPACT_UNOBSERVABLE_NOW 2u
#define RM_LEDGER_UPDATE_ALLOCATION_BYTES 0x01u
#define RM_LEDGER_UPDATE_RECOVERABILITY   0x02u
#define RM_LEDGER_UPDATE_RECOVERY_COST    0x04u
#define RM_LEDGER_UPDATE_ACCESS_IMPACT    0x08u

typedef struct rm_ledger_lease {
    uint32_t slot;
    uint32_t reserved;
    uint64_t allocation_number;
    uint64_t use_generation;
} rm_ledger_lease;

typedef struct rm_ledger_borrow {
    rm_ledger_lease lease;
    void *data;
    uint64_t length;
} rm_ledger_borrow;

typedef struct rm_ledger_attributes {
    uint32_t recoverability;
    uint32_t recovery_cost_coefficient;
    uint32_t access_loss_impact;
} rm_ledger_attributes;

typedef struct rm_ledger_update {
    uint32_t present_mask;
    uint32_t recoverability;
    uint32_t recovery_cost_coefficient;
    uint32_t access_loss_impact;
    uint64_t allocation_bytes;
} rm_ledger_update;

typedef struct rm_ledger_target {
    uint32_t kind;
    uint32_t table; /* table ID for TARGET_TABLE; zero for TARGET_PARTITION */
    const uint8_t *parent_uid;
    uint64_t parent_uid_length;
} rm_ledger_target;

typedef struct rm_ledger_partition_result {
    uint64_t allocation_number;
} rm_ledger_partition_result;

typedef struct rm_ledger_subscription {
    uint32_t table;
    uint32_t slot;
    uint64_t generation;
} rm_ledger_subscription;

typedef struct rm_ledger_table_view {
    uint64_t registered_bytes;
    uint64_t system_page_bytes;
} rm_ledger_table_view;

typedef struct rm_ledger_tick_result {
    uint64_t released;
    uint64_t scanned;
} rm_ledger_tick_result;

typedef struct rm_ledger_type_layout {
    uint32_t size;
    uint32_t alignment;
    uint32_t field_count;
    uint32_t reserved;
    uint32_t field_offsets[8];
} rm_ledger_type_layout;

typedef struct rm_ledger_descriptor {
    uint32_t descriptor_size;
    uint32_t abi_version;
    uint32_t uid_width_bits;
    uint32_t candidate_id;
    uint32_t table_count;
    uint32_t fixed_count;
    uint32_t max_subscribers_per_table;
    uint32_t resource_capacity;
    uint32_t partition_capacity;
    uint32_t index_node_count;
    uint32_t index_home_count;
    uint32_t reserved;
    uint64_t native_page_size;
    uint64_t instance_size;
    uint64_t instance_alignment;
    uint64_t operation_workspace_size;
    uint64_t operation_workspace_alignment;
    uint64_t tick_workspace_size;
    uint64_t tick_workspace_alignment;
    uint64_t destroy_workspace_size;
    uint64_t destroy_workspace_alignment;
    uint8_t structure_hash[32];
    uint8_t policy_hash[32];
    rm_ledger_type_layout lease_layout;
    rm_ledger_type_layout borrow_layout;
    rm_ledger_type_layout attributes_layout;
    rm_ledger_type_layout update_layout;
    rm_ledger_type_layout target_layout;
    rm_ledger_type_layout partition_result_layout;
    rm_ledger_type_layout subscription_layout;
    rm_ledger_type_layout table_view_layout;
    rm_ledger_type_layout tick_result_layout;
} rm_ledger_descriptor;

/* For one call, keep the instance, scratch, and every input/output range used
   by that call disjoint. Known aliases return INPUT_ERROR before an output is
   cleared or owner state changes. The ABI does not probe arbitrary pointers. */

typedef void (RM_LEDGER_CALL *rm_ledger_callback)(
    void *context,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    uint64_t allocation_number);

/* Query immutable build/layout metadata. All size/alignment fields are bytes. */
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_query_descriptor(
    rm_ledger_descriptor *out_descriptor);

/* `storage` is caller-owned, zero-initialized, and aligned to the descriptor.
   On success `out_instance` equals `storage`. Keep the block until destroy. */
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_create(
    void *storage,
    uint64_t storage_bytes,
    void **out_instance);

/* `forced` is 0 or 1. Stop new calls and drain in-flight calls before destroy.
   Active subscriptions remain rooted through destroy's synchronous committed
   notifications. Failure preserves subscriptions; release callback roots only
   after successful return. Then no further call, including a lease return, is
   valid for this instance. */
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_destroy(
    void *instance,
    uint32_t forced,
    void *destroy_workspace,
    uint64_t destroy_workspace_bytes);

/* UID arguments are exactly uid_width_bits/8 opaque bytes, including zero. */
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_use(
    void *instance,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    rm_ledger_borrow *out_borrow);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_use_fixed(
    void *instance,
    uint32_t fixed_declaration,
    rm_ledger_borrow *out_borrow);

RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_push(
    void *instance,
    const rm_ledger_target *target,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    uint32_t has_allocation_bytes,
    uint64_t allocation_bytes,
    void *operation_workspace,
    uint64_t operation_workspace_bytes,
    rm_ledger_borrow *out_borrow);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_push_partition(
    void *instance,
    const rm_ledger_target *target,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    uint32_t capacity,
    const rm_ledger_attributes *attributes,
    void *operation_workspace,
    uint64_t operation_workspace_bytes,
    rm_ledger_partition_result *out_result);

RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_end_use(
    void *instance,
    const rm_ledger_lease *lease,
    uint32_t disposition);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_update_uid(
    void *instance,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    const rm_ledger_update *update);
/* On UPDATED with UPDATE_ALLOCATION_BYTES present, out_borrow contains the
   same lease credential and refreshed region; this call acquires no lease.
   For an attribute-only update the output borrow is zero. */
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_update_lease(
    void *instance,
    const rm_ledger_lease *lease,
    const rm_ledger_update *update,
    rm_ledger_borrow *out_borrow);

RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_close(
    void *instance,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    void *operation_workspace,
    uint64_t operation_workspace_bytes);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_force_release(
    void *instance,
    const uint8_t *uid_bytes,
    uint64_t uid_length,
    void *operation_workspace,
    uint64_t operation_workspace_bytes);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_set_mode(
    void *instance,
    uint32_t table,
    uint32_t mode);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_get_table_view(
    void *instance,
    uint32_t table,
    rm_ledger_table_view *out_view);

/* Optional development-only export, absent from statistics-disabled builds.
   One coherent snapshot under the existing table lock; does not reset counters. */
typedef struct rm_ledger_development_statistics {
    uint64_t new_allocations;
    uint64_t same_epoch_releases;
    uint64_t same_epoch_allocation_bytes;
    uint64_t subpage_allocations;
    uint64_t subpage_allocation_bytes;
} rm_ledger_development_statistics;
#if defined(RM_LEDGER_DEVELOPMENT_ALLOCATION_STATISTICS) && RM_LEDGER_DEVELOPMENT_ALLOCATION_STATISTICS
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_get_development_statistics(
    void *instance,
    uint32_t table,
    rm_ledger_development_statistics *out_statistics);
#endif
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_tick(
    void *instance,
    void *tick_workspace,
    uint64_t tick_workspace_bytes,
    rm_ledger_tick_result *out_result);

/* Keep the callback and context alive until successful unsubscribe returns,
   or through successful destroy if still subscribed.
   Callbacks run synchronously without owner locks and must not reenter. */
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_subscribe(
    void *instance,
    uint32_t table,
    rm_ledger_callback callback,
    void *context,
    rm_ledger_subscription *out_subscription);
RM_LEDGER_API rm_ledger_status RM_LEDGER_CALL rm_ledger_unsubscribe(
    void *instance,
    const rm_ledger_subscription *subscription);

#ifdef __cplusplus
}
#endif
#endif /* RESOURCE_MANAGER_LEDGER_NATIVE_H */
