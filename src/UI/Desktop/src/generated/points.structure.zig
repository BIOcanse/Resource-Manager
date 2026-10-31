// Generated declaration structure.
pub const ContractSlot = struct {
    kind: []const u8,
    unit: ?[]const u8,
    integer_bit_width: ?u8,
    enumeration: ?EnumContract,
    integer_minimum: ?i64,
    integer_maximum: ?i64,
    integer_step: ?i64,
    integer_minimum_point: ?[]const u8,
    unsigned_minimum: ?u64,
    unsigned_maximum: ?u64,
    unsigned_step: ?u64,
    unsigned_maximum_point: ?[]const u8,
    decimal_minimum: ?f64,
    decimal_maximum: ?f64,
    options: []const []const u8,
    choice_options_unresolved: bool,
    required_selection_count: usize,
};
pub const ContractDescriptor = struct {
    complex_contract: ?[]const u8,
    slots: []const ContractSlot,
};
pub const EnumMember = struct { name: []const u8, literal: []const u8 };
pub const EnumContract = struct { is_unsigned: bool, bit_width: u8, members: []const EnumMember };
pub const __fpEnum_window_stop_timeout_policy = enum(u8) {
    @"terminate" = 0,
    @"wait" = 1,
};
pub const __fpEnum_overlay_render_target = enum(u8) {
    @"software" = 0,
    @"hardware" = 1,
};
pub const __fpEnum_overlay_text_rendering_mode = enum(u8) {
    @"default" = 0,
    @"aliased" = 1,
    @"gdi_classic" = 2,
    @"gdi_natural" = 3,
    @"natural" = 4,
    @"natural_symmetric" = 5,
};
pub fn Bind(comptime values: anytype) type {
    return struct {
        pub const @"controller_workers": i32 = values.@"controller_workers";
        pub const @"history_capacity": i32 = values.@"history_capacity";
        pub const @"worker_arena_bytes": i32 = values.@"worker_arena_bytes";
        pub const @"thread_stack_bytes": i32 = values.@"thread_stack_bytes";
        pub const @"backend_probe_interval_ms": i32 = values.@"backend_probe_interval_ms";
        pub const @"backend_timeout_ms": i32 = values.@"backend_timeout_ms";
        pub const @"window_cancel_poll_ms": i32 = values.@"window_cancel_poll_ms";
        pub const @"instance_connect_timeout_ms": i32 = values.@"instance_connect_timeout_ms";
        pub const @"instance_connect_poll_ms": i32 = values.@"instance_connect_poll_ms";
        pub const @"window_stop_deadline_ms": i32 = values.@"window_stop_deadline_ms";
        pub const @"window_stop_timeout_policy": __fpEnum_window_stop_timeout_policy = values.@"window_stop_timeout_policy";
        pub const @"overlay_sample_interval_ms": i32 = values.@"overlay_sample_interval_ms";
        pub const @"overlay_stream_retry_ms": i32 = values.@"overlay_stream_retry_ms";
        pub const @"overlay_render_target": __fpEnum_overlay_render_target = values.@"overlay_render_target";
        pub const @"overlay_text_rendering_mode": __fpEnum_overlay_text_rendering_mode = values.@"overlay_text_rendering_mode";
        pub const @"overlay_settings_bytes": i32 = values.@"overlay_settings_bytes";
        pub const @"overlay_maximum_software": i32 = values.@"overlay_maximum_software";
        pub const @"overlay_maximum_items": i32 = values.@"overlay_maximum_items";
        pub const @"overlay_maximum_module_software": i32 = values.@"overlay_maximum_module_software";
        pub const @"overlay_maximum_targets": i32 = values.@"overlay_maximum_targets";
        pub const @"overlay_maximum_round_targets": i32 = values.@"overlay_maximum_round_targets";
        pub const @"overlay_store_bytes": i32 = values.@"overlay_store_bytes";
        pub const @"overlay_parse_bytes": i32 = values.@"overlay_parse_bytes";
        pub const @"overlay_presentation_bytes": i32 = values.@"overlay_presentation_bytes";
        pub const @"overlay_module_bytes": i32 = values.@"overlay_module_bytes";
        pub const @"overlay_request_bytes": i32 = values.@"overlay_request_bytes";
        pub const @"overlay_settings_parse_factor": i32 = values.@"overlay_settings_parse_factor";
        pub const @"subscription_line_bytes": i32 = values.@"subscription_line_bytes";
        pub const @"subscription_parse_bytes": i32 = values.@"subscription_parse_bytes";
        pub const @"overlay_padding_px": i32 = values.@"overlay_padding_px";
        pub const @"overlay_ratio_font_minimum_px": i32 = values.@"overlay_ratio_font_minimum_px";
        pub const @"overlay_ratio_font_maximum_px": i32 = values.@"overlay_ratio_font_maximum_px";
        pub const @"overlay_maximum_dimension": i32 = values.@"overlay_maximum_dimension";
        pub const @"overlay_maximum_line_units": i32 = values.@"overlay_maximum_line_units";
        pub const @"overlay_maximum_line_bytes": i32 = values.@"overlay_maximum_line_bytes";
        pub const @"overlay_device_retries": i32 = values.@"overlay_device_retries";
        pub const @"overlay_text_layout_extent_px": i32 = values.@"overlay_text_layout_extent_px";
        pub const @"overlay_foreground_red": i32 = values.@"overlay_foreground_red";
        pub const @"overlay_foreground_green": i32 = values.@"overlay_foreground_green";
        pub const @"overlay_foreground_blue": i32 = values.@"overlay_foreground_blue";
        pub const @"overlay_foreground_alpha": i32 = values.@"overlay_foreground_alpha";
        pub const @"overlay_background_red": i32 = values.@"overlay_background_red";
        pub const @"overlay_background_green": i32 = values.@"overlay_background_green";
        pub const @"overlay_background_blue": i32 = values.@"overlay_background_blue";
        pub const @"overlay_background_alpha": i32 = values.@"overlay_background_alpha";
        pub const @"overlay_font_weight": i32 = values.@"overlay_font_weight";
        pub const @"overlay_font_style": i32 = values.@"overlay_font_style";
        pub const @"overlay_font_stretch": i32 = values.@"overlay_font_stretch";
        pub const @"overlay_text_antialias_mode": i32 = values.@"overlay_text_antialias_mode";
        pub const @"overlay_antialias_mode": i32 = values.@"overlay_antialias_mode";
        pub const @"overlay_custom_cleartype_percent": i32 = values.@"overlay_custom_cleartype_percent";
        pub const @"overlay_number_significant_digits": i32 = values.@"overlay_number_significant_digits";
        pub const @"overlay_number_fraction_digits": i32 = values.@"overlay_number_fraction_digits";
        pub const @"overlay_toggle_request_bytes": i32 = values.@"overlay_toggle_request_bytes";
        pub const @"overlay_toggle_response_bytes": i32 = values.@"overlay_toggle_response_bytes";
        pub const @"overlay_toggle_parse_bytes": i32 = values.@"overlay_toggle_parse_bytes";
        pub const @"overlay_menu_identity_bytes": i32 = values.@"overlay_menu_identity_bytes";
        pub const @"force_terminate_maximum_targets": i32 = values.@"force_terminate_maximum_targets";
        pub const @"force_terminate_maximum_candidates": i32 = values.@"force_terminate_maximum_candidates";
        pub const @"force_terminate_request_bytes": i32 = values.@"force_terminate_request_bytes";
        pub const @"force_terminate_response_bytes": i32 = values.@"force_terminate_response_bytes";
        pub const @"force_terminate_parse_bytes": i32 = values.@"force_terminate_parse_bytes";
        pub const @"overlay_font_family": []const u8 = values.@"overlay_font_family";
        pub const @"overlay_pixel_capacity_bytes": i32 = values.@"overlay_pixel_capacity_bytes";
        pub const @"ledger_tick_interval_ms": i32 = values.@"ledger_tick_interval_ms";
        pub const @"reason_text_capacity": i32 = values.@"reason_text_capacity";
    };
}
