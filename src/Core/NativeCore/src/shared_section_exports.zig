//! Consumer export anchor only. Layout, validation and seqlock stay in the package.
const shared_section = @import("rm_shared_section");

comptime {
    _ = shared_section.rm_shared_section_required_size;
    _ = shared_section.rm_shared_section_initialize;
    _ = shared_section.rm_shared_section_describe;
    _ = shared_section.rm_shared_section_source;
    _ = shared_section.rm_shared_section_position;
    _ = shared_section.rm_shared_section_write_begin;
    _ = shared_section.rm_shared_section_write_elements;
    _ = shared_section.rm_shared_section_write_length;
    _ = shared_section.rm_shared_section_write_end;
    _ = shared_section.rm_shared_section_selection_size;
    _ = shared_section.rm_shared_section_select;
    _ = shared_section.rm_shared_section_read;
    _ = shared_section.rm_shared_section_position_value;
    _ = shared_section.rm_shared_section_end;
}
