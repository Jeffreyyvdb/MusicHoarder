require 'xcodeproj'

project = Xcodeproj::Project.open('mobile/ios/App/App.xcodeproj')
app = project.targets.find { |target| target.name == 'App' }
tests = project.new_target(:ui_test_bundle, 'WidgetScreenshotTests', :ios, '17.0')
group = project.main_group.new_group('WidgetScreenshotTests', 'WidgetScreenshotTests')
tests.source_build_phase.add_file_reference(group.new_file('WidgetScreenshotTests.swift'))
tests.add_dependency(app)
tests.build_configurations.each do |config|
  config.build_settings.merge!({
    'PRODUCT_BUNDLE_IDENTIFIER' => 'com.musichoarder.screenshots',
    'PRODUCT_NAME' => '$(TARGET_NAME)',
    'GENERATE_INFOPLIST_FILE' => 'YES',
    'SWIFT_VERSION' => '5.0',
    'TEST_TARGET_NAME' => 'App',
    'TARGETED_DEVICE_FAMILY' => '1'
  })
end
project.save
scheme = Xcodeproj::XCScheme.new
scheme.add_build_target(app)
scheme.add_build_target(tests, false)
scheme.add_test_target(tests)
scheme.save_as(project.path, 'WidgetScreenshots', true)
