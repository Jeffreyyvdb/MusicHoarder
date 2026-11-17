#!/usr/bin/env ruby
# frozen_string_literal: true

# Adds the Home Screen widget extension to ios/App/App.xcodeproj, without Xcode.
#
# The project file is written by this script and committed, so the change can be read as a diff
# and made again on a machine with no Mac. Running it twice changes nothing: every object it adds
# has a fixed id (they all start with E7A1D9), which it looks up before it creates one.
#
#   gem install --user-install xcodeproj
#   ruby mobile/scripts/add-widget-target.rb
#
# What it adds:
#   - two project build settings, MH_BUNDLE_ID and MH_APP_GROUP, which every bundle id and the
#     App Group are derived from. A command-line PRODUCT_BUNDLE_IDENTIFIER= reaches every target
#     and would give the extension the app's id, so builds pass MH_BUNDLE_ID= instead;
#   - the MusicHoarderWidgets target (an app extension for iOS 17) with its sources;
#   - Shared/PlaybackCommandIntent.swift in BOTH targets (see that file for why);
#   - the WidgetBridge product of ../../plugin, linked by both targets;
#   - entitlements for both targets, and the step that embeds the extension in the app.

require 'xcodeproj'

PROJECT_PATH = File.expand_path('../ios/App/App.xcodeproj', __dir__)
WIDGETS = 'MusicHoarderWidgets'
WIDGET_SOURCES = %w[
  MusicHoarderWidgetBundle.swift NowPlayingWidget.swift LastPlayedWidget.swift WidgetParts.swift
].freeze
INTENT_SOURCE = 'PlaybackCommandIntent.swift'

Obj = Xcodeproj::Project::Object

# Every object this script owns, by name. The numbers only have to be unique and stay as they
# are. The prefix sorts after the ids already in the file, so the new build configurations are
# written below the existing ones, and that matters: `cap sync` takes the iOS version it writes
# into CapApp-SPM/Package.swift from the FIRST deployment target in this file.
IDS = %i[
  shared_group widgets_group
  intent_file app_entitlements
  bundle_file now_playing_file last_played_file parts_file
  widgets_plist widgets_entitlements widgets_privacy
  product_file
  target config_list config_debug config_release
  sources_phase frameworks_phase resources_phase
  intent_in_app intent_in_widgets
  bundle_in_widgets now_playing_in_widgets last_played_in_widgets parts_in_widgets
  privacy_in_widgets
  package bridge_for_app bridge_for_widgets bridge_in_app bridge_in_widgets
  embed_phase product_in_embed target_proxy target_dependency
].each_with_index.to_h { |name, index| [name, format('E7A1D9%018d', index + 1)] }.freeze

class Editor
  def initialize(project)
    @project = project
  end

  # The object with this name's id: the one already in the project, or a new one.
  def object(name, klass)
    uuid = IDS.fetch(name)
    @project.objects_by_uuid[uuid] || klass.new(@project, uuid).tap(&:initialize_defaults)
  end

  def add(list, item)
    list << item unless list.include?(item)
    item
  end

  def group(name, path, parent)
    group = object(name, Obj::PBXGroup)
    group.path = path
    group.source_tree = '<group>'
    add(parent.children, group)
  end

  def file(name, path, type, parent)
    file = object(name, Obj::PBXFileReference)
    file.path = path
    file.source_tree = '<group>'
    file.last_known_file_type = type
    file.include_in_index = nil
    add(parent.children, file)
  end

  def phase(name, klass, target)
    add(target.build_phases, object(name, klass))
  end

  def build_file(name, phase, file_ref: nil, product_ref: nil, settings: nil)
    build_file = object(name, Obj::PBXBuildFile)
    build_file.file_ref = file_ref if file_ref
    build_file.product_ref = product_ref if product_ref
    build_file.settings = settings if settings
    add(phase.files, build_file)
  end
end

project = Xcodeproj::Project.open(PROJECT_PATH)
edit = Editor.new(project)
app = project.targets.find { |target| target.name == 'App' } or abort('The project has no App target.')
app_group = project.main_group.children.find { |child| child.display_name == 'App' } or abort('The project has no App group.')

# --- One bundle id for every target to derive its own from ---

project.build_configurations.each do |config|
  config.build_settings['MH_BUNDLE_ID'] = 'com.musichoarder.app'
  config.build_settings['MH_APP_GROUP'] = 'group.$(MH_BUNDLE_ID)'
end

app.build_configurations.each do |config|
  settings = config.build_settings
  settings['PRODUCT_BUNDLE_IDENTIFIER'] = '$(MH_BUNDLE_ID)'
  settings['CODE_SIGN_ENTITLEMENTS'] = 'App/App.entitlements'
  # The intent the widget's buttons run is compiled into the app, which still starts on iOS 15,
  # where the AppIntents framework does not exist.
  settings['OTHER_LDFLAGS'] = ['$(inherited)', '-weak_framework', 'AppIntents']
end

# --- Files ---

shared_group = edit.group(:shared_group, 'Shared', project.main_group)
widgets_group = edit.group(:widgets_group, 'Widgets', project.main_group)

intent = edit.file(:intent_file, INTENT_SOURCE, 'sourcecode.swift', shared_group)
edit.file(:app_entitlements, 'App.entitlements', 'text.plist.entitlements', app_group)
widget_sources = WIDGET_SOURCES.zip(%i[bundle_file now_playing_file last_played_file parts_file])
                               .map { |path, name| edit.file(name, path, 'sourcecode.swift', widgets_group) }
edit.file(:widgets_plist, 'Info.plist', 'text.plist.xml', widgets_group)
edit.file(:widgets_entitlements, 'Widgets.entitlements', 'text.plist.entitlements', widgets_group)
privacy = edit.file(:widgets_privacy, 'PrivacyInfo.xcprivacy', 'text.xml', widgets_group)

product = edit.object(:product_file, Obj::PBXFileReference)
product.path = "#{WIDGETS}.appex"
product.source_tree = 'BUILT_PRODUCTS_DIR'
product.explicit_file_type = 'wrapper.app-extension'
product.include_in_index = '0'
edit.add(project.products_group.children, product)

# --- The extension target ---

widgets = edit.object(:target, Obj::PBXNativeTarget)
widgets.name = WIDGETS
widgets.product_name = WIDGETS
widgets.product_type = 'com.apple.product-type.app-extension'
widgets.product_reference = product

widget_settings = {
  'APPLICATION_EXTENSION_API_ONLY' => 'YES',
  'CODE_SIGN_ENTITLEMENTS' => 'Widgets/Widgets.entitlements',
  'CODE_SIGN_STYLE' => 'Automatic',
  'CURRENT_PROJECT_VERSION' => '1',
  'GENERATE_INFOPLIST_FILE' => 'NO',
  'INFOPLIST_FILE' => 'Widgets/Info.plist',
  # Interactive widgets (a button that runs an intent) start with iOS 17. The app stays on 15:
  # an older iPhone gets the app without its widgets.
  'IPHONEOS_DEPLOYMENT_TARGET' => '17.0',
  'LD_RUNPATH_SEARCH_PATHS' => ['$(inherited)', '@executable_path/Frameworks', '@executable_path/../../Frameworks'],
  'MARKETING_VERSION' => '1.0',
  'PRODUCT_BUNDLE_IDENTIFIER' => '$(MH_BUNDLE_ID).widgets',
  'PRODUCT_NAME' => '$(TARGET_NAME)',
  'SKIP_INSTALL' => 'YES',
  'SWIFT_VERSION' => '5.0',
  'TARGETED_DEVICE_FAMILY' => '1,2'
}.freeze

config_list = edit.object(:config_list, Obj::XCConfigurationList)
config_list.default_configuration_is_visible = '0'
config_list.default_configuration_name = 'Release'
{ config_debug: 'Debug', config_release: 'Release' }.each do |name, configuration|
  config = edit.object(name, Obj::XCBuildConfiguration)
  config.name = configuration
  config.build_settings = widget_settings.merge(
    'SWIFT_ACTIVE_COMPILATION_CONDITIONS' => configuration == 'Debug' ? 'DEBUG' : ''
  )
  edit.add(config_list.build_configurations, config)
end
widgets.build_configuration_list = config_list

sources = edit.phase(:sources_phase, Obj::PBXSourcesBuildPhase, widgets)
frameworks = edit.phase(:frameworks_phase, Obj::PBXFrameworksBuildPhase, widgets)
resources = edit.phase(:resources_phase, Obj::PBXResourcesBuildPhase, widgets)

edit.add(project.root_object.targets, widgets)
project.root_object.attributes['TargetAttributes'][widgets.uuid] = { 'ProvisioningStyle' => 'Automatic' }

widget_sources.zip(%i[bundle_in_widgets now_playing_in_widgets last_played_in_widgets parts_in_widgets])
              .each { |file, name| edit.build_file(name, sources, file_ref: file) }
edit.build_file(:privacy_in_widgets, resources, file_ref: privacy)

# The intent is a source file of both targets.
edit.build_file(:intent_in_widgets, sources, file_ref: intent)
edit.build_file(:intent_in_app, app.source_build_phase, file_ref: intent)

# --- WidgetBridge, from the plugin package ---

# The package is already in the build through CapApp-SPM, which names the same folder. This
# second reference is what lets a target of this project link one of its products directly.
package = edit.object(:package, Obj::XCLocalSwiftPackageReference)
package.relative_path = '../../plugin'
edit.add(project.root_object.package_references, package)

{ app => %i[bridge_for_app bridge_in_app],
  widgets => %i[bridge_for_widgets bridge_in_widgets] }.each do |target, (dependency_name, build_file_name)|
  dependency = edit.object(dependency_name, Obj::XCSwiftPackageProductDependency)
  dependency.package = package
  dependency.product_name = 'MusichoarderWidgetBridge'
  edit.add(target.package_product_dependencies, dependency)
  edit.build_file(build_file_name, target.frameworks_build_phase, product_ref: dependency)
end

# --- The app builds the extension first, and carries it in PlugIns/ ---

proxy = edit.object(:target_proxy, Obj::PBXContainerItemProxy)
proxy.container_portal = project.root_object.uuid
proxy.proxy_type = '1'
proxy.remote_global_id_string = widgets.uuid
proxy.remote_info = WIDGETS

dependency = edit.object(:target_dependency, Obj::PBXTargetDependency)
dependency.target = widgets
dependency.target_proxy = proxy
edit.add(app.dependencies, dependency)

embed = edit.phase(:embed_phase, Obj::PBXCopyFilesBuildPhase, app)
embed.name = 'Embed Foundation Extensions'
embed.dst_path = ''
embed.dst_subfolder_spec = '13' # PlugIns
edit.build_file(:product_in_embed, embed, file_ref: product, settings: { 'ATTRIBUTES' => ['RemoveHeadersOnCopy'] })

project.save

# --- What a mistake here would break without saying so ---

text = File.read(File.join(PROJECT_PATH, 'project.pbxproj'))

defined = text.scan(/^\t\t([0-9A-F]{24}) (?:\/\*.*?\*\/ )?= \{/).flatten
dangling = text.scan(/\b[0-9A-F]{24}\b/).uniq - defined
abort("The project names objects it does not hold: #{dangling.join(', ')}") unless dangling.empty?

first_target = text[/IPHONEOS_DEPLOYMENT_TARGET = ([0-9.]+)/, 1]
unless first_target == '15.0'
  abort("The first deployment target in the file is #{first_target}: `cap sync` would write it into CapApp-SPM.")
end

reopened = Xcodeproj::Project.open(PROJECT_PATH)
puts "#{reopened.targets.map(&:name).join(' + ')}: #{reopened.objects.count} objects"
